// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Provides a template for a <see cref="RoutingChatClient"/> that can select another client after an invocation fails.
/// </summary>
/// <remarks>
/// <para>
/// The initial client is supplied by <see cref="RoutingChatClient.SelectClientAsync"/>. An uncanceled non-streaming
/// failure causes <see cref="SelectNextClientAsync"/> to be called with the failed attempt. For streaming requests,
/// next-client selection occurs only when the failure happened before any output was exposed.
/// </para>
/// <para>
/// The base class owns invocation, streaming commitment, attempt limits, and terminal reporting. Derived classes own
/// initial and next-client selection, cross-request policy state, and the lifetime of clients they retain.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public abstract class FailoverChatClient : RoutingChatClient
{
    /// <summary>Gets or sets the maximum number of client invocations permitted for one request.</summary>
    /// <value>
    /// A positive attempt limit, or <see langword="null"/> to leave termination to next-client selection and request
    /// cancellation. The default is <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// The value is captured when a non-streaming request begins or when a streaming response begins enumeration.
    /// Changing it does not affect requests or enumerations already in progress.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not <see langword="null"/> or positive.</exception>
    public int? MaximumAttemptsPerRequest
    {
        get;
        set
        {
            if (value is <= 0)
            {
                Throw.ArgumentOutOfRangeException(nameof(value));
            }

            field = value;
        }
    }

    /// <summary>Selects the client to invoke after the previous client failed.</summary>
    /// <param name="context">The request-specific inputs.</param>
    /// <param name="previousAttempt">The pre-output failure that caused next-client selection.</param>
    /// <param name="cancellationToken">The cancellation token supplied for the request.</param>
    /// <returns>The next client to invoke, or <see langword="null"/> to stop failover.</returns>
    /// <remarks>
    /// <para>
    /// Returning <see langword="null"/> stops failover and rethrows the exception from
    /// <paramref name="previousAttempt"/>. Exceptions from this method propagate to the caller.
    /// </para>
    /// <para>
    /// If repeated failures prevent further progress and <see cref="MaximumAttemptsPerRequest"/> is
    /// <see langword="null"/>, an implementation must eventually return <see langword="null"/> or throw; otherwise
    /// selection continues until the request is canceled.
    /// </para>
    /// </remarks>
    protected abstract ValueTask<IChatClient?> SelectNextClientAsync(
        RoutingContext context,
        FailoverChatClientAttempt previousAttempt,
        CancellationToken cancellationToken);

    /// <summary>Invoked once after failover reaches a terminal outcome.</summary>
    /// <param name="context">The request-specific inputs.</param>
    /// <param name="terminalAttempt">
    /// The final client invocation outcome, or <see langword="null"/> if initial selection terminated before any
    /// client was invoked.
    /// </param>
    /// <param name="cancellationToken">The cancellation token supplied for the request.</param>
    /// <returns>A task representing the completion operation.</returns>
    /// <remarks>
    /// <para>
    /// The default implementation performs no operation. This method is called exactly once after a
    /// <see cref="RoutingContext"/> is created. <paramref name="terminalAttempt"/> is non-null whenever any client was
    /// invoked and never represents a nonterminal attempt. Nonterminal pre-output failures are supplied only to
    /// <see cref="SelectNextClientAsync"/>.
    /// </para>
    /// <para>
    /// Exceptions from this method propagate to the caller and replace any response or exception already produced by
    /// the request. Overrides should throw only when they intentionally need to replace that outcome.
    /// </para>
    /// </remarks>
    protected virtual ValueTask OnRoutingCompletedAsync(
        RoutingContext context,
        FailoverChatClientAttempt? terminalAttempt,
        CancellationToken cancellationToken) => default;

    /// <inheritdoc/>
    public sealed override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var context = new RoutingContext(messages, options);
        int? maximumAttempts = MaximumAttemptsPerRequest;
        int attemptCount = 0;
        FailoverChatClientAttempt? lastAttempt = null;

        try
        {
            while (maximumAttempts is not int limit || attemptCount < limit)
            {
                Debug.Assert(lastAttempt?.Exception is not null || lastAttempt is null,
                    "Only failed attempts should cause reselection.");
                IChatClient selectedClient;
                if (lastAttempt is null)
                {
                    selectedClient = await SelectClientAsync(context, cancellationToken);
                }
                else
                {
                    IChatClient? nextClient =
                        await SelectNextClientAsync(context, lastAttempt, cancellationToken);
                    if (nextClient is null)
                    {
                        Rethrow(lastAttempt.Exception!);
                    }

                    selectedClient = nextClient;
                }

                attemptCount++;
                ChatResponse? response = null;
                Exception? exception = null;
                long start = Stopwatch.GetTimestamp();

                try
                {
                    response = await selectedClient.GetResponseAsync(
                        context.Messages,
                        context.ChatOptions,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                lastAttempt = new FailoverChatClientAttempt(
                    selectedClient,
                    exception,
                    GetElapsedTime(start),
                    timeToFirstUpdate: null,
                    responseCompleted: exception is null,
                    outputCommitted: false);

                if (exception is null)
                {
                    return response!;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Rethrow(exception!);
                }
            }

            Debug.Assert(lastAttempt is not null, "A positive attempt limit should allow at least one attempt.");
            ThrowMaximumAttemptsReached(lastAttempt!, maximumAttempts!.Value);
            return null!;
        }
        finally
        {
            await OnRoutingCompletedAsync(context, lastAttempt, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public sealed override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = new RoutingContext(messages, options);
        int? maximumAttempts = MaximumAttemptsPerRequest;
        int attemptCount = 0;
        FailoverChatClientAttempt? lastAttempt = null;

        try
        {
            while (maximumAttempts is not int limit || attemptCount < limit)
            {
                Debug.Assert(lastAttempt?.Exception is not null || lastAttempt is null,
                    "Only failed attempts should cause reselection.");
                IChatClient selectedClient;
                if (lastAttempt is null)
                {
                    selectedClient = await SelectClientAsync(context, cancellationToken);
                }
                else
                {
                    IChatClient? nextClient =
                        await SelectNextClientAsync(context, lastAttempt, cancellationToken);
                    if (nextClient is null)
                    {
                        Rethrow(lastAttempt.Exception!);
                    }

                    selectedClient = nextClient;
                }

                attemptCount++;
                IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
                TimeSpan? timeToFirstUpdate = null;
                TimeSpan activeDuration = TimeSpan.Zero;
                bool hasCurrent;

                long operationStart = Stopwatch.GetTimestamp();
                try
                {
                    enumerator = selectedClient
                        .GetStreamingResponseAsync(
                            context.Messages,
                            context.ChatOptions,
                            cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);

                    hasCurrent = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    activeDuration += GetElapsedTime(operationStart);
                    Exception exception = (await DisposeAsync(enumerator, ex, cancellationToken))!;
                    lastAttempt = new FailoverChatClientAttempt(
                        selectedClient,
                        exception,
                        activeDuration,
                        timeToFirstUpdate: null,
                        responseCompleted: false,
                        outputCommitted: false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        Rethrow(exception);
                    }

                    continue;
                }

                activeDuration += GetElapsedTime(operationStart);
                if (hasCurrent)
                {
                    timeToFirstUpdate = activeDuration;
                }

                bool responseCompleted = false;
                bool outputCommitted = false;
                bool isTerminalAttempt = false;
                Exception? terminalException = null;

                try
                {
                    while (hasCurrent)
                    {
                        outputCommitted = true;
                        yield return enumerator.Current;

                        operationStart = Stopwatch.GetTimestamp();
                        try
                        {
                            hasCurrent = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex)
                        {
                            terminalException = ex;
                            break;
                        }
                        finally
                        {
                            activeDuration += GetElapsedTime(operationStart);
                        }
                    }

                    responseCompleted = terminalException is null;
                }
                finally
                {
                    terminalException = await DisposeAsync(enumerator, terminalException, cancellationToken);

                    lastAttempt = new FailoverChatClientAttempt(
                        selectedClient,
                        terminalException,
                        activeDuration,
                        timeToFirstUpdate,
                        responseCompleted: responseCompleted && terminalException is null,
                        outputCommitted: outputCommitted);
                    isTerminalAttempt =
                        lastAttempt.ResponseCompleted ||
                        outputCommitted ||
                        cancellationToken.IsCancellationRequested;

                    if (terminalException is not null && isTerminalAttempt)
                    {
                        Rethrow(terminalException);
                    }
                }

                if (!isTerminalAttempt)
                {
                    continue;
                }

                yield break;
            }

            Debug.Assert(lastAttempt is not null, "A positive attempt limit should allow at least one attempt.");
            ThrowMaximumAttemptsReached(lastAttempt!, maximumAttempts!.Value);
        }
        finally
        {
            await OnRoutingCompletedAsync(context, lastAttempt, cancellationToken);
        }
    }

    private static async ValueTask<Exception?> DisposeAsync(
        IAsyncDisposable? disposable,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (disposable is not null)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        return exception;
    }

    private static TimeSpan GetElapsedTime(long startingTimestamp) =>
#if NET
        Stopwatch.GetElapsedTime(startingTimestamp);
#else
        new((long)((Stopwatch.GetTimestamp() - startingTimestamp) *
            ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
#endif

    [DoesNotReturn]
    private static void ThrowMaximumAttemptsReached(
        FailoverChatClientAttempt terminalAttempt,
        int maximumAttempts)
    {
        if (terminalAttempt.Exception is { } exception)
        {
            Rethrow(exception);
        }

        throw new InvalidOperationException($"The maximum number of client attempts ({maximumAttempts}) was reached.");
    }

    [DoesNotReturn]
    private static void Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
        throw exception;
    }

}
