// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Represents the decision produced by an <see cref="IChatRouteSelector"/>.</summary>
/// <remarks>
/// A plan is the ordered list of routes the selector prefers: the first is the primary, and any remaining
/// routes are fallbacks the router tries in order if an attempt fails. A selector that naturally picks a
/// single route may return a one-route plan and leave fallback to the router (see the router's
/// <c>onFailure</c> delegate).
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatRoutePlan
{
    /// <summary>Initializes a new instance of the <see cref="ChatRoutePlan"/> class with a single route.</summary>
    /// <param name="route">The route to dispatch to.</param>
    /// <param name="decisionMetadata">Optional decision-rationale metadata the router surfaces in telemetry.</param>
    public ChatRoutePlan(
        ChatRoute route,
        IReadOnlyDictionary<string, object>? decisionMetadata = null)
        : this([Throw.IfNull(route)], decisionMetadata)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChatRoutePlan"/> class with an ordered list of routes.</summary>
    /// <param name="orderedRoutes">The routes to dispatch to, primary first followed by fallbacks.</param>
    /// <param name="decisionMetadata">Optional decision-rationale metadata the router surfaces in telemetry.</param>
    public ChatRoutePlan(
        IReadOnlyList<ChatRoute> orderedRoutes,
        IReadOnlyDictionary<string, object>? decisionMetadata = null)
    {
        _ = Throw.IfNull(orderedRoutes);
        if (orderedRoutes.Count == 0)
        {
            Throw.ArgumentException(nameof(orderedRoutes), "A route plan must contain at least one route.");
        }

        var copy = new ChatRoute[orderedRoutes.Count];
        for (int i = 0; i < orderedRoutes.Count; i++)
        {
            copy[i] = Throw.IfNull(orderedRoutes[i]);
        }

        OrderedRoutes = new ReadOnlyCollection<ChatRoute>(copy);
        DecisionMetadata = decisionMetadata;
    }

    /// <summary>Gets the routes to dispatch to, primary first followed by fallbacks tried in order on failure.</summary>
    public IReadOnlyList<ChatRoute> OrderedRoutes { get; }

    /// <summary>Gets optional decision-rationale metadata describing why the selector chose this plan.</summary>
    /// <remarks>
    /// A selector may annotate its decision with signals such as a complexity tier or a semantic similarity
    /// score. The router surfaces each entry as a tag on its <c>routing.decision</c>
    /// activity event (named by <c>RoutingChatClient.DecisionEventName</c>),
    /// so it is observable through any OpenTelemetry trace exporter without affecting routing behavior.
    /// </remarks>
    public IReadOnlyDictionary<string, object>? DecisionMetadata { get; }
}
