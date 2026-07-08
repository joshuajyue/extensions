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

/// <summary>
/// A <see cref="DelegatingChatClient"/> that routes each request among several <em>profiles</em> of a single inner
/// client, chosen by a swappable selection policy.
/// </summary>
/// <remarks>
/// <para>
/// This is the single-client dual of <see cref="RoutingChatClient"/>. Where <see cref="RoutingChatClient"/> is a
/// multiplexer that fans out to <em>many</em> inner clients (one per route), <see cref="DelegatingRoutingChatClient"/>
/// wraps <em>one</em> inner client and treats its routes as pure metadata: a selector picks a route, and the router
/// shapes the request accordingly — supplying the route's <see cref="ChatRoute.ModelId"/> and
/// <see cref="ChatRoute.ReasoningEffort"/> — before forwarding to that same inner client. This is the idiomatic seam
/// when a provider honors a per-request <see cref="ChatOptions.ModelId"/> (so one client can serve many models) and
/// lets routing compose in a builder pipeline via <see cref="DelegatingRoutingChatClientBuilderExtensions.UseRouting"/>.
/// Because it delegates, it preserves the inner client's identity: <see cref="GetService"/> and the reported
/// <see cref="ChatClientMetadata"/> pass through, so downstream telemetry sees the real provider rather than a
/// synthetic routing layer.
/// </para>
/// <para>
/// The routing behavior — the capability gate, the single selector call per request, the fallback walk on
/// pre-commit failure, and all telemetry — is identical to <see cref="RoutingChatClient"/> because both drive the
/// same internal engine. Routes here are not bound to a client (any <see cref="ChatRoute.Client"/> is ignored); the
/// wrapped inner client is always the dispatch target. See <see cref="RoutingChatClient"/> for the semantics of the
/// <c>selector</c>, <c>onFailure</c>, and <c>canRoute</c> parameters, which behave identically here.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class DelegatingRoutingChatClient : DelegatingChatClient
{
    private readonly RouteDispatchLoop _loop;

    // The per-route dispatch closures handed to the shared RouteDispatchLoop. Unlike the RoutingChatClient closures
    // — which forward to each route's own bound client — these always forward to this middleware's single inner
    // client after applying the chosen route's advisory options (via the shared RouteForwarding.Apply). They capture
    // InnerClient, so they are per-instance rather than static.
    private readonly Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _dispatch;
    private readonly Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamingDispatch;

    /// <summary>Initializes a new instance of the <see cref="DelegatingRoutingChatClient"/> class.</summary>
    /// <param name="innerClient">The single client every route dispatches to, shaped per the chosen route's metadata.</param>
    /// <param name="routes">The routes to dispatch between. At least one is required; each is metadata only (no bound client is needed).</param>
    /// <param name="selector">The selection policy, or <see langword="null"/> for the opinion-free default. See <see cref="RoutingChatClient"/>.</param>
    /// <param name="onFailure">An optional failure delegate consulted on a pre-commit dispatch failure. See <see cref="RoutingChatClient"/>.</param>
    /// <param name="canRoute">An optional candidate filter narrowing which routes the router may consider per request. See <see cref="RoutingChatClient"/>.</param>
    public DelegatingRoutingChatClient(
        IChatClient innerClient,
        IReadOnlyList<ChatRoute> routes,
        IChatRouteSelector? selector = null,
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure = null,
        Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, bool>? canRoute = null)
        : base(innerClient)
    {
        _loop = new RouteDispatchLoop(ValidateRoutes(routes), selector, onFailure, canRoute);
        _dispatch = (route, messages, options, cancellationToken) =>
            InnerClient.GetResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken);
        _streamingDispatch = (route, messages, options, cancellationToken) =>
            InnerClient.GetStreamingResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken);
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _loop.RunAsync(messages, options, _dispatch, cancellationToken);

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _loop.RunStreamingAsync(messages, options, _streamingDispatch, cancellationToken);

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        // Expose the selector for introspection, then defer to the base: base.GetService returns this when the
        // requested type matches and otherwise passes through to the inner client, so the wrapped client's identity
        // (including its ChatClientMetadata) is reported honestly rather than masked by a synthetic routing layer.
        IChatRouteSelector? selector = _loop.Selector;
        if (serviceKey is null && selector is not null && serviceType.IsInstanceOfType(selector))
        {
            return selector;
        }

        return base.GetService(serviceType, serviceKey);
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
}
