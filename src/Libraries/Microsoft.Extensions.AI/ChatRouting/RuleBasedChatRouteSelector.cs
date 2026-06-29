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
/// A deterministic selection policy that applies hard eligibility gates and then ranks the surviving
/// models by cost and latency. A model is eligible when it declares every <see cref="RoutingChatModelTraits"/>
/// capability the request requires (for example tool calling when tools are supplied) and its context
/// window can fit the approximately estimated prompt; eligible models are then ordered by lower cost,
/// then lower latency.
/// </summary>
/// <remarks>
/// This is a starting-point heuristic intended for experimentation. Traits are used purely as a
/// <i>capability gate</i> — a correctness filter — and never as a quality or performance signal: a
/// model is not preferred for advertising more capabilities than the request needs, because modern
/// chat models largely share the same capability flags and so cannot be ranked on quality by them.
/// The produced plan is the full set of models in ranked order, so the best-ranked model is the
/// primary route and the rest act as fallbacks. Ineligible models — those missing a required
/// capability or whose <see cref="RoutingChatModel.MaxInputTokens"/> cannot fit the prompt — are
/// ranked last so they are only used as a last resort. Ties preserve the original registration order.
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
        // Hard gates first. A model that lacks a capability the request requires, or whose context
        // window cannot fit the prompt, is guaranteed to fail, so eligible models always rank above
        // ineligible ones. Capability (traits) is strictly a correctness gate here, never a quality
        // signal: a model is not rewarded for advertising more traits than the request needs.
        bool leftEligible = IsEligible(left, requiredTraits, estimatedPromptTokens);
        bool rightEligible = IsEligible(right, requiredTraits, estimatedPromptTokens);
        if (leftEligible != rightEligible)
        {
            return leftEligible ? 1 : -1;
        }

        // Among equally (in)eligible models, prefer lower cost, then lower latency.
        int costComparison = CompareLowerIsBetter(GetTotalTokenCost(left), GetTotalTokenCost(right));
        if (costComparison != 0)
        {
            return costComparison;
        }

        return CompareLowerIsBetter(left.TypicalLatency, right.TypicalLatency);
    }

    // A model passes the hard gates when it declares every required capability and its context window
    // can fit the prompt. This is an all-or-nothing correctness check, not a ranking.
    private static bool IsEligible(RoutingChatModel model, RoutingChatModelTraits requiredTraits, int estimatedPromptTokens) =>
        HasRequiredTraits(model.Traits, requiredTraits) && Fits(model, estimatedPromptTokens);

    // The capability gate: a model satisfies it only when it declares every trait the request
    // requires. Extra traits beyond those required do not make a model rank higher.
    private static bool HasRequiredTraits(RoutingChatModelTraits modelTraits, RoutingChatModelTraits requiredTraits) =>
        (modelTraits & requiredTraits) == requiredTraits;

    // A model fits when its context window is unknown (cannot prove it will not fit) or large enough.
    private static bool Fits(RoutingChatModel model, int estimatedPromptTokens) =>
        model.MaxInputTokens is not int maxInputTokens || estimatedPromptTokens <= maxInputTokens;

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
