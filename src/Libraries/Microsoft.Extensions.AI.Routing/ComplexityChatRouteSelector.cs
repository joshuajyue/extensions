// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A deterministic selection policy that classifies each request into a <see cref="ChatComplexityTier"/>
/// using rule-based keyword and pattern scoring, then routes to the model the caller mapped to that tier.
/// </summary>
/// <remarks>
/// <para>
/// This follows the rule-based approach of ClawRouter (which LiteLLM's complexity router is also based
/// on): it scores the request across several weighted
/// dimensions (prompt length, code keywords, reasoning markers, technical terms, simple indicators,
/// multi-step patterns, and question complexity), maps the score to a tier, and resolves the tier to
/// an explicitly configured model. Single-word keywords match on word boundaries; the system prompt
/// contributes to the code, technical, and simple signals while reasoning markers consider only the
/// user message. Scoring requires no external calls and is sub-millisecond.
/// </para>
/// <para>
/// The tier-to-model mapping is supplied explicitly at construction (keyed by the model's
/// <see cref="RoutingChatModel.Name"/>). When a tier has no mapping, the optional default model is
/// used. A complexity classifier picks exactly one model, so the produced <see cref="ChatRoutePlan"/>
/// contains just that model: it has no meaningful ranking of the other models to offer as fallbacks.
/// Configure fallback on the router instead (see <c>RoutingChatClientBuilder.UseFallback()</c>),
/// which owns failure handling. Tune the scoring with <see cref="ComplexityRouterOptions"/>.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ComplexityChatRouteSelector : IChatRouteSelector
{
    private const int CharactersPerToken = 4;

    // Per-dimension keyword-bucket thresholds and scores, matching the LiteLLM complexity router.
    // Each dimension scores 0 below its low threshold, its low score at/above the low threshold, and
    // its high score at/above the high threshold (by distinct-keyword match count).
    private const int CodeLowThreshold = 1;
    private const int CodeHighThreshold = 2;
    private const double CodeLowScore = 0.5;
    private const double CodeHighScore = 1.0;
    private const int ReasoningLowThreshold = 1;
    private const int ReasoningHighThreshold = 2;
    private const double ReasoningLowScore = 0.7;
    private const double ReasoningHighScore = 1.0;
    private const int TechnicalLowThreshold = 2;
    private const int TechnicalHighThreshold = 4;
    private const double TechnicalLowScore = 0.5;
    private const double TechnicalHighScore = 1.0;
    private const int SimpleLowThreshold = 1;
    private const int SimpleHighThreshold = 2;
    private const double SimpleLowScore = -1.0;
    private const double SimpleHighScore = -1.0;
    private const double ShortTokenScore = -1.0;
    private const double LongTokenScore = 1.0;
    private const double MultiStepHitScore = 0.5;
    private const int QuestionCountThreshold = 3;
    private const double QuestionHitScore = 0.5;

    private static readonly TimeSpan _regexTimeout = TimeSpan.FromMilliseconds(100);

    // Decision-rationale key surfaced as a routing.decision tag (string tier name). Kept private: the
    // tag schema is a telemetry detail observed through ActivityListener, not a programmatic API surface.
    private const string ComplexityTierMetadataKey = "routing.complexity.tier";

    private readonly Dictionary<ChatComplexityTier, string> _modelByTier;
    private readonly string? _defaultModel;
    private readonly ComplexityRouterOptions _options;
    private readonly Regex[] _multiStepPatterns;

    /// <summary>Initializes a new instance of the <see cref="ComplexityChatRouteSelector"/> class.</summary>
    /// <param name="modelByTier">
    /// The explicit mapping from a complexity tier to the <see cref="RoutingChatModel.Name"/> to route to.
    /// </param>
    /// <param name="defaultModel">
    /// The optional model name to use when the computed tier has no entry in <paramref name="modelByTier"/>.
    /// </param>
    /// <param name="options">The optional scoring configuration. Defaults mirror the LiteLLM complexity router.</param>
    public ComplexityChatRouteSelector(
        IReadOnlyDictionary<ChatComplexityTier, string> modelByTier,
        string? defaultModel = null,
        ComplexityRouterOptions? options = null)
    {
        _ = Throw.IfNull(modelByTier);
        if (modelByTier.Count == 0)
        {
            Throw.ArgumentException(nameof(modelByTier), "At least one tier-to-model mapping must be provided.");
        }

        _modelByTier = modelByTier.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        _defaultModel = defaultModel;
        _options = options ?? new ComplexityRouterOptions();

        var multiStep = new List<Regex>(_options.MultiStepPatterns.Count);
        foreach (string pattern in _options.MultiStepPatterns)
        {
            multiStep.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout));
        }

        _multiStepPatterns = multiStep.ToArray();
    }

    /// <inheritdoc/>
    public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        var ctx = Throw.IfNull(context);

        (string? userText, string? systemText) = ExtractUserAndSystem(ctx.Messages);

        ChatComplexityTier tier;
        string? targetName;
        if (userText is null)
        {
            // Faithful to LiteLLM: with no user message there is nothing to score, so this classifies as
            // Medium and prefers the explicit default model, falling back to whatever is mapped to Medium.
            tier = ChatComplexityTier.Medium;
            targetName = _defaultModel ?? (_modelByTier.TryGetValue(ChatComplexityTier.Medium, out string? medium) ? medium : null);
        }
        else
        {
            tier = Classify(userText, systemText, _options, _multiStepPatterns);
            targetName = _modelByTier.TryGetValue(tier, out string? mapped) ? mapped : _defaultModel;
        }

        RoutingChatModel target = SelectTarget(ctx.Models, targetName);

        var metadata = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ComplexityTierMetadataKey] = tier.ToString(),
        };

        return new ValueTask<ChatRoutePlan>(new ChatRoutePlan(target, metadata));
    }

    /// <summary>Classifies the request into a complexity tier using the configured rule-based scoring.</summary>
    /// <param name="messages">The chat messages being routed.</param>
    /// <returns>
    /// The computed <see cref="ChatComplexityTier"/>. A request with no user message classifies as
    /// <see cref="ChatComplexityTier.Medium"/>.
    /// </returns>
    public ChatComplexityTier ClassifyTier(IEnumerable<ChatMessage> messages)
    {
        (string? userText, string? systemText) = ExtractUserAndSystem(Throw.IfNull(messages));
        return userText is null ? ChatComplexityTier.Medium : Classify(userText, systemText, _options, _multiStepPatterns);
    }

    private static ChatComplexityTier Classify(
        string userText,
        string? systemText,
        ComplexityRouterOptions options,
        Regex[] multiStepPatterns)
    {
        // The code, technical, and simple signals also look at the system prompt for deployment context.
        // Reasoning markers look at the user message only, so a system prompt cannot force the reasoning
        // tier. Token count and question count are based on the user message.
        string fullText = ((systemText ?? string.Empty) + " " + userText).ToUpperInvariant();
        string userUpper = userText.ToUpperInvariant();

        int estimatedTokens = userText.Length / CharactersPerToken;
        int reasoningMatches = CountMatches(userUpper, options.ReasoningMarkers);

        double weighted =
            (TokenScore(estimatedTokens, options) * options.TokenCountWeight) +
            (BucketScore(CountMatches(fullText, options.CodeKeywords), CodeLowThreshold, CodeHighThreshold, CodeLowScore, CodeHighScore) * options.CodePresenceWeight) +
            (BucketScore(reasoningMatches, ReasoningLowThreshold, ReasoningHighThreshold, ReasoningLowScore, ReasoningHighScore) * options.ReasoningMarkersWeight) +
            (BucketScore(CountMatches(fullText, options.TechnicalTerms), TechnicalLowThreshold, TechnicalHighThreshold, TechnicalLowScore, TechnicalHighScore) * options.TechnicalTermsWeight) +
            (BucketScore(CountMatches(fullText, options.SimpleIndicators), SimpleLowThreshold, SimpleHighThreshold, SimpleLowScore, SimpleHighScore) * options.SimpleIndicatorsWeight) +
            (MultiStepScore(fullText, multiStepPatterns) * options.MultiStepPatternsWeight) +
            (QuestionScore(userText) * options.QuestionComplexityWeight);

        // Special behavior: two or more reasoning markers force the reasoning tier regardless of score.
        if (reasoningMatches >= options.ReasoningMarkerOverrideCount)
        {
            return ChatComplexityTier.Reasoning;
        }

        if (weighted < options.SimpleToMediumThreshold)
        {
            return ChatComplexityTier.Simple;
        }

        if (weighted < options.MediumToComplexThreshold)
        {
            return ChatComplexityTier.Medium;
        }

        if (weighted < options.ComplexToReasoningThreshold)
        {
            return ChatComplexityTier.Complex;
        }

        return ChatComplexityTier.Reasoning;
    }

    private static double TokenScore(int estimatedTokens, ComplexityRouterOptions options)
    {
        if (estimatedTokens < options.ShortPromptTokens)
        {
            return ShortTokenScore;
        }

        return estimatedTokens > options.LongPromptTokens ? LongTokenScore : 0.0;
    }

    private static double MultiStepScore(string text, Regex[] patterns)
    {
        foreach (Regex pattern in patterns)
        {
            if (pattern.IsMatch(text))
            {
                return MultiStepHitScore;
            }
        }

        return 0.0;
    }

    private static double BucketScore(int matchCount, int lowThreshold, int highThreshold, double lowScore, double highScore)
    {
        if (matchCount >= highThreshold)
        {
            return highScore;
        }

        return matchCount >= lowThreshold ? lowScore : 0.0;
    }

    private static double QuestionScore(string text)
    {
        int questionMarks = 0;
        foreach (char c in text)
        {
            if (c == '?')
            {
                questionMarks++;
            }
        }

        return questionMarks > QuestionCountThreshold ? QuestionHitScore : 0.0;
    }

    // Counts the distinct keywords/phrases present in the (already lower-cased) text. Single-word
    // keywords match on word boundaries (so "api" does not match "capital"); multi-word phrases match
    // as substrings, mirroring the LiteLLM behavior.
    private static int CountMatches(string text, IReadOnlyList<string> keywords)
    {
        int count = 0;
        foreach (string keyword in keywords)
        {
            if (Matches(text, keyword))
            {
                count++;
            }
        }

        return count;
    }

    private static bool Matches(string text, string keyword)
    {
        string normalized = keyword.ToUpperInvariant();
        return normalized.IndexOf(" ", StringComparison.Ordinal) < 0 ? ContainsWord(text, normalized) : text.Contains(normalized, StringComparison.Ordinal);
    }

    private static bool ContainsWord(string text, string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        int index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            bool leftBoundary = index == 0 || !IsWordChar(text[index - 1]);
            int end = index + word.Length;
            bool rightBoundary = end == text.Length || !IsWordChar(text[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Extracts the last user message and last system prompt, matching how the LiteLLM router resolves text.
    private static (string? UserText, string? SystemText) ExtractUserAndSystem(IEnumerable<ChatMessage> messages)
    {
        string? userText = null;
        string? systemText = null;
        foreach (ChatMessage message in messages)
        {
            string text = message.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (message.Role == ChatRole.User)
            {
                userText = text;
            }
            else if (message.Role == ChatRole.System)
            {
                systemText = text;
            }
        }

        return (userText, systemText);
    }

    // Complexity classifies a request into exactly one tier, so the plan is a single model: the one mapped
    // to that tier. When the mapping is absent or names a model that is not registered, the first registered
    // model is used as a deterministic, opinion-free default. The router owns any fallback (see UseFallback),
    // because a tier classifier has no meaningful ranking of the other models to offer.
    private static RoutingChatModel SelectTarget(IReadOnlyList<RoutingChatModel> models, string? targetName)
    {
        if (targetName is not null)
        {
            foreach (RoutingChatModel model in models)
            {
                if (string.Equals(model.Name, targetName, StringComparison.Ordinal))
                {
                    return model;
                }
            }
        }

        return models[0];
    }
}
