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
/// Layers conversation stickiness onto any inner selection policy: each request it asks an app-supplied
/// callback for the ordered route names the request is pinned to, and prefers them when they still resolve to
/// registered routes; otherwise it defers to the inner policy.
/// </summary>
/// <remarks>
/// <para>
/// Stickiness is an application concern — both the STATE (which route a conversation is pinned to) and the
/// TRIGGER (when to pin and release) belong to the app — so this policy holds neither. The <c>getPins</c>
/// callback is invoked per request and returns the ordered route names to prefer, typically read from
/// conversation state keyed by an app-owned stable session id (not <c>ChatOptions.ConversationId</c>, which some
/// providers rotate per message). Returning <see langword="null"/> or empty means "not pinned this turn".
/// </para>
/// <para>
/// A pin that no longer resolves to a registered route is skipped, so a stale pin can never dead-end a request
/// and re-attaches for free once the route exists again. The initial selection returns the first resolved pin;
/// fallback walks the remaining pins, then defers to the inner policy.
/// </para>
/// </remarks>
public sealed class StickyRoutingClient : RoutingChatClient
{
    /// <summary>Chooses the next route the same way <see cref="RoutingChatClient.SelectNextRouteAsync"/> does.</summary>
    public delegate ValueTask<ChatRoute?> InnerSelector(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken);

    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<string>?> _getPins;
    private readonly InnerSelector _inner;

    public StickyRoutingClient(
        IReadOnlyList<ChatRoute> routes,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<string>?> getPins,
        InnerSelector inner)
        : base(routes)
    {
        _getPins = getPins ?? throw new ArgumentNullException(nameof(getPins));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? pins = _getPins(messages, options);
        if (pins is { Count: > 0 })
        {
            // Resolve pins to registered routes (by name), preserving order and skipping stale/attempted ones.
            ChatRoute? pinned = pins
                .Select(name => routes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(r => r is not null && !attempted.Contains(r));

            if (pinned is not null)
            {
                return new(pinned);
            }
        }

        // No pin applies this turn (or all pins have been tried): defer to the inner policy.
        return _inner(messages, options, routes, attempted, lastException, cancellationToken);
    }
}
