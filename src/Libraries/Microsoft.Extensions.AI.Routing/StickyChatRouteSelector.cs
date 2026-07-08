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
/// A selection policy that layers conversation stickiness onto any inner <see cref="IChatRouteSelector"/>: each
/// turn it asks an app-supplied callback for the ordered route names this request is pinned to, and pins to
/// them when they still resolve to current candidates; otherwise it defers to the inner policy.
/// </summary>
/// <remarks>
/// <para>
/// Stickiness is an application policy: both its STATE (which route a conversation is pinned to) and its TRIGGER
/// (when to pin and when to release) belong to the app, so this selector holds neither. Instead the
/// <c>getPins</c> callback is invoked once per request and returns the ordered route names to prefer — typically
/// read from conversation state keyed by the request. Returning <see langword="null"/> or an empty list means
/// "not pinned this turn", deferring entirely to the inner selector.
/// </para>
/// <para>
/// Each pin is resolved against the request's live <see cref="ChatRouteContext.Routes"/> by
/// <see cref="ChatRoute.Name"/> (case-insensitive), and the resolved instance is the same object the router
/// matches by reference identity — this selector never reconstructs a <see cref="ChatRoute"/>. A pin to a route
/// that is not a current candidate (for example one filtered out for a missing capability, or hidden by the
/// router's <c>canRoute</c> availability filter while it cools) simply does not resolve and is skipped, so a
/// stale pin can never dead-end a turn and re-attaches for free once the route is a candidate again. When at
/// least one pin resolves, the resolved routes (in the callback's order, de-duplicated) become the plan and the
/// decision is tagged with <see cref="PinnedDecisionKey"/> for telemetry; when none resolve, the inner selector
/// decides.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class StickyChatRouteSelector : IChatRouteSelector
{
    /// <summary>
    /// The <see cref="ChatRoutePlan.DecisionMetadata"/> key set to <see langword="true"/> when a turn was routed
    /// by a resolved pin rather than the inner selector. Surfaced as a <c>routing.decision</c> telemetry tag.
    /// </summary>
    public const string PinnedDecisionKey = "routing.pinned";

    private static readonly IReadOnlyDictionary<string, object> _pinnedDecision =
        new Dictionary<string, object>(StringComparer.Ordinal) { [PinnedDecisionKey] = true };

    private readonly Func<ChatRouteContext, IReadOnlyList<string>?> _getPins;
    private readonly IChatRouteSelector _inner;

    /// <summary>Initializes a new instance of the <see cref="StickyChatRouteSelector"/> class.</summary>
    /// <param name="getPins">
    /// A callback invoked once per request that returns the ordered route names this request is pinned to, or
    /// <see langword="null"/>/empty to defer to <paramref name="inner"/>. Typically reads app-owned conversation state.
    /// </param>
    /// <param name="inner">The selection policy to use when no pin resolves to a current candidate.</param>
    public StickyChatRouteSelector(Func<ChatRouteContext, IReadOnlyList<string>?> getPins, IChatRouteSelector inner)
    {
        _getPins = Throw.IfNull(getPins);
        _inner = Throw.IfNull(inner);
    }

    /// <inheritdoc/>
    public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(context);

        IReadOnlyList<string>? pins = _getPins(context);
        if (pins is { Count: > 0 })
        {
            List<ChatRoute>? hits = null;
            foreach (string name in pins)
            {
                ChatRoute? hit = ResolveByName(context.Routes, name);
                if (hit is not null && (hits is null || !hits.Contains(hit)))
                {
                    (hits ??= new List<ChatRoute>(pins.Count)).Add(hit);
                }
            }

            if (hits is not null)
            {
                return new ValueTask<ChatRoutePlan>(new ChatRoutePlan(hits, _pinnedDecision));
            }
        }

        return _inner.SelectRouteAsync(context, cancellationToken);
    }

    private static ChatRoute? ResolveByName(IReadOnlyList<ChatRoute> routes, string name)
    {
        foreach (ChatRoute route in routes)
        {
            if (string.Equals(route.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return route;
            }
        }

        return null;
    }
}
