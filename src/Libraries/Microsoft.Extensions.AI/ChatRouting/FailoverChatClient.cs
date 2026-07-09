// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A <see cref="RoutingChatClient"/> that tries its routes in order until one succeeds, honoring an explicitly
/// requested model first.
/// </summary>
/// <remarks>
/// <para>
/// This is the built-in, opinion-free routing policy. Its selection is deterministic:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// For the initial route, if the request pins a <see cref="ChatOptions.ModelId"/> that matches a route's
/// <see cref="ChatRoute.ModelId"/> or <see cref="ChatRoute.Name"/> (case-insensitively), that route is chosen;
/// otherwise the first registered route is chosen.
/// </description>
/// </item>
/// <item>
/// <description>
/// After a route fails before committing any output, the next registered route not yet attempted — in
/// registration order — is chosen, so the routes form a fallback chain. When every route has been attempted,
/// the last exception is rethrown.
/// </description>
/// </item>
/// </list>
/// <para>
/// A cancellation is never treated as a failure and never triggers fallback. For streaming, fallback applies only
/// until the first update is produced; once a token is on the wire the router never re-routes. For a different
/// policy — complexity- or cost-aware selection, health-based candidate filtering, provider-aware failure pruning —
/// derive from <see cref="RoutingChatClient"/> and override
/// <see cref="RoutingChatClient.SelectNextRouteAsync"/> directly.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class FailoverChatClient : RoutingChatClient
{
    /// <summary>Initializes a new instance of the <see cref="FailoverChatClient"/> class.</summary>
    /// <param name="routes">The routes to dispatch between, in fallback order. At least one is required, each bound to an <see cref="IChatClient"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routes"/> is empty, contains a duplicate name, or contains a route with no bound client.</exception>
    public FailoverChatClient(IReadOnlyList<ChatRoute> routes)
        : base(routes)
    {
    }

    /// <inheritdoc/>
    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // Initial selection: honor an explicit ModelId, otherwise the first registered route.
        if (attempted.Count == 0)
        {
            return new ValueTask<ChatRoute?>(SelectInitialRoute(routes, options?.ModelId));
        }

        // Fallback: the next registered route not yet attempted, in registration order.
        foreach (ChatRoute route in routes)
        {
            if (!attempted.Contains(route))
            {
                return new ValueTask<ChatRoute?>(route);
            }
        }

        // Every route attempted: stop and let the router rethrow the last exception.
        return new ValueTask<ChatRoute?>((ChatRoute?)null);
    }

    private static ChatRoute SelectInitialRoute(IReadOnlyList<ChatRoute> routes, string? modelId)
    {
        if (!string.IsNullOrEmpty(modelId))
        {
            foreach (ChatRoute route in routes)
            {
                if (string.Equals(route.ModelId, modelId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.Name, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return route;
                }
            }
        }

        return routes[0];
    }
}
