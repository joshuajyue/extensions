// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Illustrative cookbook sample. Not part of the build; see docs/routing-chat-client-cookbook.md.
#pragma warning disable MEAI001 // Routing types are experimental.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.AI.Samples.Routing;

/// <summary>
/// Tries routes in order until one succeeds, honoring an explicitly requested model first. This is the
/// simplest useful policy: the initial selection honors a pinned <see cref="ChatOptions.ModelId"/> (matched
/// by a route's <see cref="ChatRoute.ModelId"/> or <see cref="ChatRoute.Name"/>) and otherwise picks the first
/// registered route; each later call falls back to the next registered route not yet attempted.
/// </summary>
public sealed class OrderedFailoverClient : RoutingChatClient
{
    public OrderedFailoverClient(IReadOnlyList<ChatRoute> routes)
        : base(routes)
    {
    }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // First call: honor a pinned ModelId (matched by ModelId or Name), else the first route.
        if (attempted.Count == 0)
        {
            ChatRoute? pinned = options?.ModelId is { } id
                ? routes.FirstOrDefault(r =>
                    string.Equals(r.ModelId, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase))
                : null;

            return new(pinned ?? routes[0]);
        }

        // Later calls: the next registered route not yet attempted; null when the chain is exhausted.
        return new(routes.Except(attempted).FirstOrDefault());
    }
}
