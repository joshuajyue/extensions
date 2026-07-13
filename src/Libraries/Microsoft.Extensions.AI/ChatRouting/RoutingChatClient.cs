// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

#pragma warning disable SA1204 // Static members should appear before non-static members

namespace Microsoft.Extensions.AI;

/// <summary>
/// An <see cref="IChatClient"/> that routes each request to one of several inner models chosen by a subclass's
/// selection policy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutingChatClient"/> is the routing <em>mechanism</em>: it owns the candidate routes, drives the
/// per-request attempt loop, and walks fallbacks on failure. It holds
/// no opinion about which model is better — that is entirely delegated to a subclass through the single
/// <see cref="SelectRouteAsync"/> method (the <em>policy</em>). Everything a routing decision needs — which
/// route to try first, whether to narrow the candidates, and what to try next after a failure — collapses into
/// that one method: the base calls it once to choose the primary route, then again after each pre-commit failure
/// to choose a fallback, passing the routes already attempted and the exception that just occurred.
/// </para>
/// <para>
/// The method returns the next route to attempt, or <see langword="null"/> to stop. When it returns
/// <see langword="null"/> on the very first call, the router has no route to invoke and throws. When it returns
/// <see langword="null"/> after one or more failures, the router rethrows the last exception. The policy is
/// responsible for returning a usable route and deciding whether a previously attempted route should be retried.
/// A cancellation is never treated as a route failure and never reaches the policy. For streaming this applies
/// only before the first update is produced; once a token is on the wire the router never re-routes.
/// </para>
/// <para>
/// Before dispatch, the selected route's <see cref="ChatRoute.ModelId"/> and
/// <see cref="ChatRoute.ReasoningEffort"/> are applied as request defaults on a clone of the caller's
/// <see cref="ChatOptions"/>. Explicit caller values take precedence.
/// </para>
/// <para>
/// Because each route is bound to its own <see cref="IChatClient"/>, a routing pipeline forms a tree: a route's
/// client may have its own middleware, or may itself be another <see cref="RoutingChatClient"/>.
/// </para>
/// <para>
/// The simplest policy is ordered failover — honor an explicit <see cref="ChatOptions.ModelId"/> else the first
/// route, then each remaining route in registration order on failure — which a subclass expresses in a handful of
/// lines: on the first call (<c>attempted</c> empty) return the pinned-or-first route; on a later call return the
/// first route not in <c>attempted</c>, or <see langword="null"/> when none remain. Richer policies — complexity-
/// or cost-aware selection, health-based candidate filtering, provider-aware failure pruning — fall out of the
/// same method by reading the request, the route metadata, or <c>lastException</c>.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public abstract class RoutingChatClient : IChatClient
{
    private const string NoRouteMessage = "Routing produced no route to invoke.";

    // The routes this router owns, retained so Dispose can dispose each bound client exactly once and so the
    // selection policy can be handed the registered set as a read-only list.
    private readonly ChatRoute[] _routes;
    private readonly ReadOnlyCollection<ChatRoute> _routeList;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClient"/> class.</summary>
    /// <param name="routes">The routes to dispatch between. At least one is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routes"/> is empty or contains a duplicate name.</exception>
    protected RoutingChatClient(IReadOnlyList<ChatRoute> routes)
    {
        _routes = ValidateRoutes(routes);
        _routeList = new ReadOnlyCollection<ChatRoute>(_routes);
    }

    /// <summary>Gets the routes registered with this router.</summary>
    protected IReadOnlyList<ChatRoute> Routes => _routeList;

    /// <summary>
    /// When overridden in a derived class, chooses the next route to attempt for a request, or <see langword="null"/>
    /// to stop routing.
    /// </summary>
    /// <param name="messages">The chat messages being routed.</param>
    /// <param name="options">The chat options for the request, if any.</param>
    /// <param name="routes">The routes registered with the router.</param>
    /// <param name="attempted">
    /// The routes already attempted this request, in attempt order. Empty on the first call (the initial selection);
    /// on later calls the last entry is the route that just failed.
    /// </param>
    /// <param name="lastException">
    /// <see langword="null"/> on the first call. On later calls, the unclassified exception the most recently
    /// attempted route threw before committing any output. The policy owns any interpretation of it — for example
    /// mapping an HTTP status code to a cooldown, or pruning every route that shares the failed route's provider on
    /// an authentication error.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// The next route to attempt, or <see langword="null"/> to stop. Returning <see langword="null"/> on the first
    /// call throws, as the router has no route to invoke; returning <see langword="null"/> after a failure rethrows
    /// <paramref name="lastException"/>.
    /// </returns>
    protected abstract ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken);

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        var attempted = new List<ChatRoute>();
        ChatRoute? route = await SelectRouteAsync(messages, options, _routeList, attempted, lastException: null, cancellationToken);
        if (route is null)
        {
            throw new InvalidOperationException(NoRouteMessage);
        }

        while (true)
        {
            attempted.Add(route);

            try
            {
                return await route.Client.GetResponseAsync(messages, ApplyRouteDefaults(route, options), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ChatRoute? next = await SelectRouteAsync(messages, options, _routeList, attempted, ex, cancellationToken);
                if (next is null)
                {
                    throw;
                }

                route = next;
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        var attempted = new List<ChatRoute>();
        ChatRoute? route = await SelectRouteAsync(messages, options, _routeList, attempted, lastException: null, cancellationToken);
        if (route is null)
        {
            throw new InvalidOperationException(NoRouteMessage);
        }

        while (true)
        {
            attempted.Add(route);

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                route.Client.GetStreamingResponseAsync(messages, ApplyRouteDefaults(route, options), cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                // Failure handling applies only until the first update is produced.
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ChatRoute? next = await SelectRouteAsync(messages, options, _routeList, attempted, ex, cancellationToken);
                await enumerator.DisposeAsync();

                if (next is null)
                {
                    throw;
                }

                route = next;
                continue;
            }

            // The first token commits this model. Mid-stream failures past here are surfaced to the caller rather
            // than treated as routing failures.
            try
            {
                while (hasFirst)
                {
                    yield return enumerator.Current;
                    hasFirst = await enumerator.MoveNextAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            yield break;
        }
    }

    /// <inheritdoc/>
    public virtual object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Disposes the current instance and all route chat clients, ensuring that each client is disposed only once.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the current instance and all route chat clients, ensuring that each client is disposed only once.</summary>
    /// <param name="disposing"><see langword="true"/> if being called from <see cref="Dispose()"/>; otherwise, <see langword="false"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            HashSet<IChatClient> disposedClients = [];
            foreach (ChatRoute route in _routes)
            {
                IChatClient client = route.Client;
                if (disposedClients.Add(client))
                {
                    client.Dispose();
                }
            }
        }
    }

    private static ChatRoute[] ValidateRoutes(IReadOnlyList<ChatRoute> routes)
    {
        _ = Throw.IfNull(routes);

        if (routes.Count == 0)
        {
            Throw.ArgumentException(nameof(routes), "At least one route must be provided.");
        }

        var result = new ChatRoute[routes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            result[i] = Throw.IfNull(routes[i]);
            for (int j = 0; j < i; j++)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(result[j].Name, result[i].Name))
                {
                    Throw.ArgumentException(nameof(routes), $"A route named '{result[i].Name}' has already been added.");
                }
            }
        }

        return result;
    }

    private static ChatOptions? ApplyRouteDefaults(ChatRoute route, ChatOptions? options)
    {
        bool needsModelId = route.ModelId is not null && options?.ModelId is null;
        bool needsEffort = route.ReasoningEffort is not null && options?.Reasoning?.Effort is null;
        if (!needsModelId && !needsEffort)
        {
            return options;
        }

        ChatOptions configured = options?.Clone() ?? new ChatOptions();
        if (needsModelId)
        {
            configured.ModelId = route.ModelId;
        }

        if (needsEffort)
        {
            (configured.Reasoning ??= new ReasoningOptions()).Effort = route.ReasoningEffort;
        }

        return configured;
    }
}
