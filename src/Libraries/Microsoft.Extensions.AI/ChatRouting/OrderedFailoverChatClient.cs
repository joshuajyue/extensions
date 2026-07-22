// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides ordered failover across a sequence of chat clients.</summary>
/// <remarks>
/// <para>
/// The clients are tried in order. An invocation failure before streaming output is exposed advances to the next
/// client. Cancellation and failures after streaming output is exposed are propagated without failover.
/// </para>
/// <para>
/// The configured clients are snapshotted by the constructor and must contain unique object references. When every
/// client has failed, the final failure is rethrown.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class OrderedFailoverChatClient : FailoverChatClient
{
    private readonly bool _leaveOpen;
    private readonly IChatClient[] _clients;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="OrderedFailoverChatClient"/> class.</summary>
    /// <param name="clients">The clients to invoke, in fallback order.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave inner clients open when this instance is disposed;
    /// otherwise, <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="clients"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clients"/> is empty, contains <see langword="null"/>, or contains the same client instance more than once.
    /// </exception>
    public OrderedFailoverChatClient(IReadOnlyList<IChatClient> clients, bool leaveOpen = false)
    {
        _ = Throw.IfNull(clients);
        _leaveOpen = leaveOpen;
        if (clients.Count == 0)
        {
            Throw.ArgumentException(nameof(clients), "At least one client must be provided.");
        }

        _clients = [.. clients];
        for (int i = 0; i < _clients.Length; i++)
        {
            if (_clients[i] is null)
            {
                Throw.ArgumentException(nameof(clients), "Clients must not contain null.");
            }

            for (int j = 0; j < i; j++)
            {
                if (ReferenceEquals(_clients[j], _clients[i]))
                {
                    Throw.ArgumentException(nameof(clients), "Each client instance must be unique.");
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        return new(_clients[0]);
    }

    /// <inheritdoc/>
    protected override ValueTask<IChatClient?> SelectNextClientAsync(
        RoutingContext context,
        FailoverChatClientAttempt previousAttempt,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        if (previousAttempt.Exception is null)
        {
            throw new InvalidOperationException("Failover reselection requires a failed previous attempt.");
        }

        int previousIndex = IndexOfClient(previousAttempt.Client);
        if (previousIndex < 0)
        {
            throw new InvalidOperationException("The previous attempt used a client that is not configured for failover.");
        }

        return previousIndex + 1 < _clients.Length
            ? new(_clients[previousIndex + 1])
            : new((IChatClient?)null);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (disposing && !_leaveOpen)
            {
                foreach (IChatClient client in _clients)
                {
                    client.Dispose();
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private int IndexOfClient(IChatClient client)
    {
        for (int i = 0; i < _clients.Length; i++)
        {
            if (ReferenceEquals(_clients[i], client))
            {
                return i;
            }
        }

        return -1;
    }
}
