// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Configures the deterministic, rule-based scoring used by a <see cref="ComplexityChatRouteSelector"/>.</summary>
/// <remarks>
/// All defaults mirror the LiteLLM complexity router (dimension weights, tier boundaries, and token
/// thresholds). Every dimension weight, threshold, and keyword set is overridable so the scoring can
/// be tuned without changing the selector. Scoring performs no I/O and runs in sub-millisecond time.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ComplexityRouterOptions
{
    /// <summary>Gets or sets the weight of the prompt-length signal. The default is <c>0.10</c>.</summary>
    public double TokenCountWeight { get; set; } = 0.10;

    /// <summary>Gets or sets the weight of the code-keyword signal. The default is <c>0.30</c>.</summary>
    public double CodePresenceWeight { get; set; } = 0.30;

    /// <summary>Gets or sets the weight of the reasoning-marker signal. The default is <c>0.25</c>.</summary>
    public double ReasoningMarkersWeight { get; set; } = 0.25;

    /// <summary>Gets or sets the weight of the technical-term signal. The default is <c>0.25</c>.</summary>
    public double TechnicalTermsWeight { get; set; } = 0.25;

    /// <summary>Gets or sets the weight of the simple-indicator signal, which lowers the score. The default is <c>0.05</c>.</summary>
    public double SimpleIndicatorsWeight { get; set; } = 0.05;

    /// <summary>Gets or sets the weight of the multi-step-pattern signal. The default is <c>0.03</c>.</summary>
    public double MultiStepPatternsWeight { get; set; } = 0.03;

    /// <summary>Gets or sets the weight of the question-complexity signal. The default is <c>0.02</c>.</summary>
    public double QuestionComplexityWeight { get; set; } = 0.02;

    /// <summary>Gets or sets the score at or above which a request is at least <see cref="ChatComplexityTier.Medium"/>. The default is <c>0.15</c>.</summary>
    public double SimpleToMediumThreshold { get; set; } = 0.15;

    /// <summary>Gets or sets the score at or above which a request is at least <see cref="ChatComplexityTier.Complex"/>. The default is <c>0.35</c>.</summary>
    public double MediumToComplexThreshold { get; set; } = 0.35;

    /// <summary>Gets or sets the score at or above which a request is <see cref="ChatComplexityTier.Reasoning"/>. The default is <c>0.60</c>.</summary>
    public double ComplexToReasoningThreshold { get; set; } = 0.60;

    /// <summary>Gets or sets the token count below which a prompt counts as short (and is penalized as simple). The default is <c>15</c>.</summary>
    public int ShortPromptTokens { get; set; } = 15;

    /// <summary>Gets or sets the token count above which a prompt counts as long (and is boosted as complex). The default is <c>400</c>.</summary>
    public int LongPromptTokens { get; set; } = 400;

    /// <summary>Gets or sets the number of distinct reasoning markers that forces the <see cref="ChatComplexityTier.Reasoning"/> tier. The default is <c>2</c>.</summary>
    public int ReasoningMarkerOverrideCount { get; set; } = 2;

    /// <summary>Gets or sets the keywords that indicate code-related work. Single-word entries are matched on word boundaries; multi-word entries are matched as substrings.</summary>
    public IReadOnlyList<string> CodeKeywords { get; set; } =
    [
        "function", "class", "def", "const", "let", "var", "import", "export", "return",
        "async", "await", "try", "catch", "exception", "error", "debug", "api", "endpoint",
        "request", "response", "database", "sql", "query", "schema", "algorithm", "implement",
        "refactor", "optimize", "python", "javascript", "typescript", "java", "rust", "golang",
        "react", "vue", "angular", "node", "docker", "kubernetes", "git", "commit", "merge",
        "branch", "pull request",
    ];

    /// <summary>Gets or sets the phrases that indicate explicit reasoning is requested. Scored against the user message only.</summary>
    public IReadOnlyList<string> ReasoningMarkers { get; set; } =
    [
        "step by step", "think through", "let's think", "reason through", "analyze this",
        "break down", "explain your reasoning", "show your work", "chain of thought",
        "think carefully", "consider all", "evaluate", "pros and cons", "compare and contrast",
        "weigh the options", "logical", "deduce", "infer", "conclude",
    ];

    /// <summary>Gets or sets the domain terms that indicate technical complexity. Single-word entries are matched on word boundaries; multi-word entries are matched as substrings.</summary>
    public IReadOnlyList<string> TechnicalTerms { get; set; } =
    [
        "architecture", "distributed", "scalable", "microservice", "machine learning",
        "neural network", "deep learning", "encryption", "authentication", "authorization",
        "performance", "latency", "throughput", "benchmark", "concurrency", "parallel",
        "threading", "memory", "cpu", "gpu", "optimization", "protocol", "tcp", "http",
        "grpc", "websocket", "container", "orchestration",
    ];

    /// <summary>Gets or sets the phrases that indicate a simple request, lowering the score. Single-word entries are matched on word boundaries; multi-word entries are matched as substrings.</summary>
    public IReadOnlyList<string> SimpleIndicators { get; set; } =
    [
        "what is", "what's", "define", "definition of", "who is", "who was", "when did",
        "when was", "where is", "where was", "how many", "how much", "yes or no",
        "true or false", "simple", "brief", "short", "quick", "hello", "hi", "hey",
        "thanks", "thank you", "goodbye", "bye", "okay",
    ];

    /// <summary>Gets or sets the regular-expression patterns that indicate a multi-step request. Matching is case-insensitive; any match contributes the multi-step signal.</summary>
    public IReadOnlyList<string> MultiStepPatterns { get; set; } =
    [
        "first.*?then", @"step\s*\d", @"\d+\.\s", @"[a-z]\)\s",
    ];
}
