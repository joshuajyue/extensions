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
/// A deterministic, metadata-driven selection policy. Ranks the available models using their
/// declared <see cref="RoutingChatModelTraits"/>, context window, cost, and latency, inferring
/// required traits and the prompt size from the request.
/// </summary>
/// <remarks>
/// This is a starting-point heuristic intended for experimentation. The produced plan is the full
/// set of models in ranked order, so the best-ranked model is the primary route and the rest act as
/// fallbacks. Models whose <see cref="RoutingChatModel.MaxInputTokens"/> cannot fit the (approximately
/// estimated) prompt are ranked last so they are only used as a last resort. Ties preserve the
/// original registration order.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class RuleBasedChatRouteSelector : IChatRouteSelector
{
    private const int CharactersPerToken = 4;

    /// <summary>Gets a shared instance of the <see cref="RuleBasedChatRouteSelector"/>.</summary>
    public static RuleBasedChatRouteSelector Instance { get; } = new();

    /// <inheritdoc/>
    public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        var ctx = Microsoft.Shared.Diagnostics.Throw.IfNull(context);

        RoutingChatModelTraits requiredTraits = GetRequiredTraits(ctx.Options);
        int estimatedPromptTokens = EstimatePromptTokens(ctx.Messages);

        var comparer = Comparer<RoutingChatModel>.Create(
            (left, right) => CompareModels(left, right, requiredTraits, estimatedPromptTokens));

        // OrderByDescending is a stable sort, so equally-ranked models keep their registration order.
        RoutingChatModel[] ranked = ctx.Models.OrderByDescending(model => model, comparer).ToArray();
        return new ValueTask<ChatRoutePlan>(new ChatRoutePlan(ranked));
    }

    private static int CompareModels(
        RoutingChatModel left,
        RoutingChatModel right,
        RoutingChatModelTraits requiredTraits,
        int estimatedPromptTokens)
    {
        // A model whose context window cannot fit the prompt is guaranteed to fail, so fit is the
        // strongest signal: fitting models always rank above non-fitting ones.
        int fitComparison = CompareContextFit(left, right, estimatedPromptTokens);
        if (fitComparison != 0)
        {
            return fitComparison;
        }

        int traitComparison = CompareRequiredTraits(left.Traits, right.Traits, requiredTraits);
        if (traitComparison != 0)
        {
            return traitComparison;
        }

        int costComparison = CompareLowerIsBetter(GetTotalTokenCost(left), GetTotalTokenCost(right));
        if (costComparison != 0)
        {
            return costComparison;
        }

        return CompareLowerIsBetter(left.TypicalLatency, right.TypicalLatency);
    }

    private static int CompareContextFit(RoutingChatModel left, RoutingChatModel right, int estimatedPromptTokens)
    {
        bool leftFits = Fits(left, estimatedPromptTokens);
        bool rightFits = Fits(right, estimatedPromptTokens);

        return leftFits == rightFits ? 0 : leftFits ? 1 : -1;
    }

    // A model fits when its context window is unknown (cannot prove it will not fit) or large enough.
    private static bool Fits(RoutingChatModel model, int estimatedPromptTokens) =>
        model.MaxInputTokens is not int maxInputTokens || estimatedPromptTokens <= maxInputTokens;

    private static int CompareRequiredTraits(
        RoutingChatModelTraits leftTraits,
        RoutingChatModelTraits rightTraits,
        RoutingChatModelTraits requiredTraits)
    {
        if (requiredTraits == RoutingChatModelTraits.None)
        {
            return 0;
        }

        bool leftHasTraits = (leftTraits & requiredTraits) == requiredTraits;
        bool rightHasTraits = (rightTraits & requiredTraits) == requiredTraits;

        return leftHasTraits == rightHasTraits ? 0 : leftHasTraits ? 1 : -1;
    }

    private static decimal? GetTotalTokenCost(RoutingChatModel model) =>
        model.InputTokenCostPerMillion + model.OutputTokenCostPerMillion;

    private static int CompareLowerIsBetter(decimal? left, decimal? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return right is null ? 1 : -left.Value.CompareTo(right.Value);
    }

    private static int CompareLowerIsBetter(TimeSpan? left, TimeSpan? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return right is null ? 1 : -left.Value.CompareTo(right.Value);
    }

    private static RoutingChatModelTraits GetRequiredTraits(ChatOptions? options)
    {
        RoutingChatModelTraits requiredTraits = RoutingChatModelTraits.None;

        if (options?.Tools is { Count: > 0 })
        {
            requiredTraits |= RoutingChatModelTraits.ToolCalling;
        }

        if (options?.Reasoning is not null)
        {
            requiredTraits |= RoutingChatModelTraits.Reasoning;
        }

        return requiredTraits;
    }

    // An approximate prompt token count (~4 characters per token) used only to compare against a
    // model's advertised context window. This is intentionally a cheap heuristic, not a tokenizer.
    private static int EstimatePromptTokens(IEnumerable<ChatMessage> messages)
    {
        long characters = 0;
        foreach (ChatMessage message in messages)
        {
            characters += message.Text.Length;
        }

        return (int)Math.Min(int.MaxValue, (characters + CharactersPerToken - 1) / CharactersPerToken);
    }
}
