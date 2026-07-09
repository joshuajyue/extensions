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

/// <summary>The complexity tier a request is classified into.</summary>
public enum ComplexityTier
{
    /// <summary>A short, trivial request (a greeting or a definition lookup).</summary>
    Simple,

    /// <summary>An everyday request of moderate complexity.</summary>
    Medium,

    /// <summary>A demanding request involving code, technical terms, or multiple steps.</summary>
    Complex,

    /// <summary>A request that calls for explicit multi-step reasoning.</summary>
    Reasoning,
}

/// <summary>
/// A deterministic policy that classifies each request into a <see cref="ComplexityTier"/> with rule-based
/// keyword and pattern scoring (no model call), then routes to the route the caller mapped to that tier. This
/// mirrors the rule-based approach of LiteLLM's complexity router: it scores the request across weighted signals
/// (prompt length, code keywords, reasoning markers, technical terms, simple indicators, multi-step patterns,
/// and question count), maps the score to a tier, and resolves the tier to a configured route. Classification is
/// sub-millisecond. On failure it falls back through the remaining routes in registration order.
/// </summary>
public sealed class ComplexityRoutingClient : RoutingChatClient
{
    private const int CharactersPerToken = 4;

    private static readonly string[] _codeKeywords =
    [
        "function", "class", "def", "const", "let", "var", "import", "export", "return", "async", "await",
        "try", "catch", "exception", "error", "debug", "api", "endpoint", "request", "response", "database",
        "sql", "query", "schema", "algorithm", "implement", "refactor", "optimize", "python", "javascript",
        "typescript", "java", "rust", "golang", "react", "docker", "kubernetes", "git", "commit", "pull request",
    ];

    private static readonly string[] _reasoningMarkers =
    [
        "step by step", "think through", "let's think", "reason through", "analyze this", "break down",
        "explain your reasoning", "show your work", "chain of thought", "pros and cons", "compare and contrast",
        "weigh the options", "deduce", "infer", "conclude",
    ];

    private static readonly string[] _technicalTerms =
    [
        "architecture", "distributed", "scalable", "microservice", "machine learning", "neural network",
        "deep learning", "encryption", "authentication", "authorization", "performance", "latency", "throughput",
        "concurrency", "parallel", "threading", "protocol", "grpc", "websocket", "container", "orchestration",
    ];

    private static readonly string[] _simpleIndicators =
    [
        "what is", "what's", "define", "who is", "when did", "where is", "how many", "yes or no",
        "true or false", "simple", "brief", "short", "quick", "hello", "hi", "hey", "thanks", "goodbye",
    ];

    private readonly IReadOnlyDictionary<ComplexityTier, string> _routeByTier;
    private readonly string? _defaultRoute;

    /// <summary>Initializes the client with an explicit tier-to-route-name mapping and an optional default route.</summary>
    public ComplexityRoutingClient(
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyDictionary<ComplexityTier, string> routeByTier,
        string? defaultRoute = null)
        : base(routes)
    {
        _routeByTier = routeByTier ?? throw new ArgumentNullException(nameof(routeByTier));
        _defaultRoute = defaultRoute;
    }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // Fallback: a tier classifier picks one route, so on failure just try the next untried route in order.
        if (attempted.Count > 0)
        {
            return new(routes.Except(attempted).FirstOrDefault());
        }

        // First call: classify and resolve the tier's route (or the default, or the first route).
        ComplexityTier tier = Classify(messages);
        string? targetName = _routeByTier.TryGetValue(tier, out string? mapped) ? mapped : _defaultRoute;

        ChatRoute? target = targetName is null
            ? null
            : routes.FirstOrDefault(r => string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));

        return new(target ?? routes[0]);
    }

    /// <summary>Classifies the request's complexity. A request with no user text classifies as <see cref="ComplexityTier.Medium"/>.</summary>
    public static ComplexityTier Classify(IEnumerable<ChatMessage> messages)
    {
        string? userText = null;
        string? systemText = null;
        foreach (ChatMessage message in messages)
        {
            if (string.IsNullOrEmpty(message.Text))
            {
                continue;
            }

            if (message.Role == ChatRole.User)
            {
                userText = message.Text;
            }
            else if (message.Role == ChatRole.System)
            {
                systemText = message.Text;
            }
        }

        if (userText is null)
        {
            return ComplexityTier.Medium;
        }

        // Code, technical, and simple signals also consider the system prompt; reasoning looks at the user text.
        string fullText = ((systemText ?? string.Empty) + " " + userText).ToUpperInvariant();
        string userUpper = userText.ToUpperInvariant();
        int estimatedTokens = userText.Length / CharactersPerToken;
        int reasoningMatches = CountMatches(userUpper, _reasoningMarkers);

        double score =
            (TokenScore(estimatedTokens) * 0.10) +
            (Bucket(CountMatches(fullText, _codeKeywords), low: 1, high: 2, lowScore: 0.5, highScore: 1.0) * 0.30) +
            (Bucket(reasoningMatches, low: 1, high: 2, lowScore: 0.7, highScore: 1.0) * 0.25) +
            (Bucket(CountMatches(fullText, _technicalTerms), low: 2, high: 4, lowScore: 0.5, highScore: 1.0) * 0.25) +
            (Bucket(CountMatches(fullText, _simpleIndicators), low: 1, high: 2, lowScore: -1.0, highScore: -1.0) * 0.05) +
            (QuestionScore(userText) * 0.02);

        // Two or more reasoning markers force the reasoning tier regardless of the weighted score.
        if (reasoningMatches >= 2)
        {
            return ComplexityTier.Reasoning;
        }

        return score switch
        {
            < 0.15 => ComplexityTier.Simple,
            < 0.35 => ComplexityTier.Medium,
            < 0.60 => ComplexityTier.Complex,
            _ => ComplexityTier.Reasoning,
        };
    }

    private static double TokenScore(int estimatedTokens) => estimatedTokens switch
    {
        < 15 => -1.0,
        > 400 => 1.0,
        _ => 0.0,
    };

    private static double Bucket(int matchCount, int low, int high, double lowScore, double highScore)
    {
        if (matchCount >= high)
        {
            return highScore;
        }

        return matchCount >= low ? lowScore : 0.0;
    }

    private static double QuestionScore(string text) => text.Count(c => c == '?') > 3 ? 0.5 : 0.0;

    // Counts distinct keywords present in the (upper-cased) text; single words match on word boundaries.
    private static int CountMatches(string text, string[] keywords)
    {
        int count = 0;
        foreach (string keyword in keywords)
        {
            string k = keyword.ToUpperInvariant();
            bool present = k.Contains(' ') ? text.Contains(k, StringComparison.Ordinal) : ContainsWord(text, k);
            if (present)
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsWord(string text, string word)
    {
        int index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = index == 0 || !IsWordChar(text[index - 1]);
            int end = index + word.Length;
            bool rightOk = end == text.Length || !IsWordChar(text[end]);
            if (leftOk && rightOk)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
