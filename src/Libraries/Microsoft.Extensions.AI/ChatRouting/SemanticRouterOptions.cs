// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Configures the embedding-based routing performed by a <see cref="SemanticChatRouteSelector"/>.</summary>
/// <remarks>
/// All defaults mirror the LiteLLM semantic router (its underlying <c>semantic-router</c> library):
/// the globally highest <see cref="TopK"/> utterance matches are kept, grouped by model, and combined
/// with <see cref="Aggregation"/>; a model is then chosen only when its aggregated score meets
/// <see cref="ScoreThreshold"/> (or its per-model override in <see cref="ScoreThresholdByModel"/>).
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class SemanticRouterOptions
{
    /// <summary>
    /// Gets or sets the number of globally highest-scoring utterance matches considered when scoring
    /// models. The default is <c>5</c>, matching the LiteLLM semantic router.
    /// </summary>
    /// <remarks>
    /// Selection is global across every model's utterances, not per model: a model only contributes to
    /// the decision when at least one of its utterances is among the top <see cref="TopK"/> matches.
    /// </remarks>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Gets or sets how a model's matched utterance scores are combined into a single per-model score.
    /// The default is <see cref="SemanticRouteAggregation.Mean"/>, matching the LiteLLM semantic router.
    /// </summary>
    public SemanticRouteAggregation Aggregation { get; set; } = SemanticRouteAggregation.Mean;

    /// <summary>
    /// Gets or sets the minimum aggregated cosine similarity a model must reach to be selected. The
    /// default is <c>0.3</c>, matching the LiteLLM semantic router's default encoder threshold.
    /// </summary>
    /// <remarks>
    /// Cosine similarity ranges from -1 to 1. When no model reaches this threshold, the selector routes
    /// to its default model (or the first registered model). Set a value of 0 or below to always route
    /// to the best-scoring model.
    /// </remarks>
    public float ScoreThreshold { get; set; } = 0.3f;

    /// <summary>
    /// Gets or sets optional per-model score thresholds keyed by the model's
    /// <see cref="RoutingChatModel.Name"/>. A model present here uses its own threshold instead of
    /// <see cref="ScoreThreshold"/>, matching the per-route <c>score_threshold</c> of the LiteLLM
    /// semantic router. The default is <see langword="null"/> (every model uses <see cref="ScoreThreshold"/>).
    /// </summary>
    public IReadOnlyDictionary<string, float>? ScoreThresholdByModel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a sticky decision is re-evaluated and re-routed when the pinned
    /// model's similarity to a later turn falls below its selection threshold. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This only has an effect under a sticky <see cref="RoutingStickiness"/> scope (for example
    /// <see cref="RoutingStickiness.ByConversation"/>), where a decision would otherwise be reused unchanged.
    /// When enabled, the selector attaches a confidence <em>floor</em>: a decision that won by meeting its
    /// threshold keeps being reused while the pinned model still scores at or above that same threshold for later
    /// turns (its per-model override in <see cref="ScoreThresholdByModel"/>, else <see cref="ScoreThreshold"/>),
    /// and is re-run once the conversation drifts below it. The floor reuses the selection threshold, so
    /// <see cref="ScoreThreshold"/> (and any per-model override) is the single, modifiable knob. When disabled
    /// (the default), a sticky decision is truly frozen for its scope.
    /// </remarks>
    public bool ReselectBelowThreshold { get; set; }
}
