// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Represents the decision produced by an <see cref="IChatRouteSelector"/>.</summary>
/// <remarks>
/// A plan is the ordered list of models the selector prefers: the first is the primary, and any remaining
/// models are fallbacks the router tries in order if an attempt fails. A selector that naturally picks a
/// single model may return a one-model plan and leave fallback to the router (see
/// <see cref="RoutingChatClientBuilder.UseFallback()"/>). The optional <see cref="RemainsValid"/> predicate
/// lets the selector author its own invalidation rule for cached decisions (see
/// <see cref="RoutingStickiness"/>); when it returns <see langword="false"/>, the router re-runs the
/// selector even when a sticky decision is cached.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatRoutePlan
{
    /// <summary>Initializes a new instance of the <see cref="ChatRoutePlan"/> class with a single model.</summary>
    /// <param name="model">The model to route to.</param>
    /// <param name="remainsValid">An optional predicate that determines whether a cached decision is still valid.</param>
    /// <param name="decisionMetadata">Optional decision-rationale metadata the router surfaces in telemetry.</param>
    public ChatRoutePlan(
        RoutingChatModel model,
        Func<ChatRouteContext, CancellationToken, ValueTask<bool>>? remainsValid = null,
        IReadOnlyDictionary<string, object>? decisionMetadata = null)
        : this([Throw.IfNull(model)], remainsValid, decisionMetadata)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChatRoutePlan"/> class with an ordered list of models.</summary>
    /// <param name="orderedModels">The models to route to, primary first followed by fallbacks.</param>
    /// <param name="remainsValid">An optional predicate that determines whether a cached decision is still valid.</param>
    /// <param name="decisionMetadata">Optional decision-rationale metadata the router surfaces in telemetry.</param>
    public ChatRoutePlan(
        IReadOnlyList<RoutingChatModel> orderedModels,
        Func<ChatRouteContext, CancellationToken, ValueTask<bool>>? remainsValid = null,
        IReadOnlyDictionary<string, object>? decisionMetadata = null)
    {
        _ = Throw.IfNull(orderedModels);
        if (orderedModels.Count == 0)
        {
            Throw.ArgumentException(nameof(orderedModels), "A route plan must contain at least one model.");
        }

        var copy = new RoutingChatModel[orderedModels.Count];
        for (int i = 0; i < orderedModels.Count; i++)
        {
            copy[i] = Throw.IfNull(orderedModels[i]);
        }

        OrderedModels = new ReadOnlyCollection<RoutingChatModel>(copy);
        RemainsValid = remainsValid;
        DecisionMetadata = decisionMetadata;
    }

    /// <summary>Gets the models to route to, primary first followed by fallbacks tried in order on failure.</summary>
    public IReadOnlyList<RoutingChatModel> OrderedModels { get; }

    /// <summary>Gets the optional predicate that determines whether a cached decision is still valid.</summary>
    /// <remarks>
    /// When <see langword="null"/>, a cached decision sticks for its <see cref="RoutingStickiness"/> scope.
    /// When non-<see langword="null"/>, the router awaits it before reusing a cached decision and re-runs
    /// the selector if it returns <see langword="false"/>.
    /// </remarks>
    public Func<ChatRouteContext, CancellationToken, ValueTask<bool>>? RemainsValid { get; }

    /// <summary>Gets optional decision-rationale metadata describing why the selector chose this plan.</summary>
    /// <remarks>
    /// A selector may annotate its decision with signals such as a complexity tier or a semantic similarity
    /// score. The router surfaces each entry as a tag on its <c>routing.decision</c>
    /// <see cref="System.Diagnostics.ActivityEvent"/> (see <see cref="RoutingChatClient.DecisionEventName"/>),
    /// so it is observable through any OpenTelemetry trace exporter without affecting routing behavior.
    /// </remarks>
    public IReadOnlyDictionary<string, object>? DecisionMetadata { get; }
}
