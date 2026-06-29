// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Specifies how a <see cref="SemanticChatRouteSelector"/> combines the similarity scores of a model's
/// matched profile utterances into a single per-model score.
/// </summary>
/// <remarks>
/// These mirror the aggregation methods of the LiteLLM semantic router (its underlying
/// <c>semantic-router</c> library): <c>sum</c>, <c>mean</c>, and <c>max</c>. Aggregation is applied
/// only over the matches that survive the global top-k selection (see
/// <see cref="SemanticRouterOptions.TopK"/>).
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public enum SemanticRouteAggregation
{
    /// <summary>Sum the model's matched utterance scores. Favors models that match in many ways.</summary>
    Sum,

    /// <summary>Average the model's matched utterance scores. This is the default, matching LiteLLM.</summary>
    Mean,

    /// <summary>Take the model's single highest matched utterance score.</summary>
    Max,
}
