// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

#pragma warning disable SA1204 // Static members should appear before non-static members

namespace Microsoft.Extensions.AI;

// Behavior tests for the routing sample policies under docs/samples/routing/*.cs. Each policy is inlined here as a
// trimmed-but-faithful RoutingChatClient subclass (the samples are illustrative and not part of the build) so the
// tests are self-contained, offline, and deterministic — no live network or real model calls. RoutingChatClientTests
// covers the base dispatch mechanism (fallback, disposal, nesting) and the OrderedFailover
// policy; this file adds the per-policy DECISION tests the other samples motivate: for each policy, at least one
// happy-path selection test and one fallback-on-failure test.
public class RoutingPolicySamplesTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    // A client that answers with text identifying the route it represents.
    private static TestChatClient Ok(string text = "ok") =>
        new() { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))) };

    private static TestChatClient Fails(string message = "boom") =>
        new() { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException(message) };

    private static TestChatClient StreamsOk(params string[] texts) =>
        new() { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates(texts) };

    private static TestChatClient StreamThrows() =>
        new() { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldUpdates(params string[] texts)
    {
        foreach (string text in texts)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowingStream()
    {
        await Task.Yield();
        foreach (int _ in Array.Empty<int>())
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "never");
        }

        throw new InvalidOperationException("boom");
    }

    private static string SelectedRoute(ChatResponse response) => response.Text;

    // ------------------------------------------------------------------------
    // ComplexityRoutingClient — rule-based complexity tiering (pure Classify).
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("hi", ComplexityTier.Simple)]
    [InlineData("thanks a lot", ComplexityTier.Simple)]
    [InlineData("Explain how a REST API endpoint works and describe when a database query would be appropriate for this use case.", ComplexityTier.Medium)]
    [InlineData("Refactor this async database query function to optimize the microservice architecture and reduce latency.", ComplexityTier.Complex)]
    [InlineData("Let's think step by step and reason through the trade-offs.", ComplexityTier.Reasoning)]
    public void Complexity_Classify_MapsTextToTier(string text, ComplexityTier expected) =>
        Assert.Equal(expected, ComplexityRouter.Classify([User(text)]));

    [Fact]
    public void Complexity_Classify_NoUserText_IsMedium() =>
        Assert.Equal(ComplexityTier.Medium, ComplexityRouter.Classify([new ChatMessage(ChatRole.System, "You are a helpful assistant.")]));

    [Fact]
    public async Task Complexity_RoutesRequestToItsTierRoute()
    {
        using var small = Ok("small");
        using var mid = Ok("mid");
        using var big = Ok("big");
        using var reasoner = Ok("reasoner");
        var map = new Dictionary<ComplexityTier, string>
        {
            [ComplexityTier.Simple] = "small",
            [ComplexityTier.Medium] = "mid",
            [ComplexityTier.Complex] = "big",
            [ComplexityTier.Reasoning] = "reasoner",
        };

        using var client = new ComplexityRouter(
        [
            new ChatRoute("small", client: small),
            new ChatRoute("mid", client: mid),
            new ChatRoute("big", client: big),
            new ChatRoute("reasoner", client: reasoner),
        ], map);

        // "hi" classifies Simple, so the request routes to the Simple-tier route.
        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("small", SelectedRoute(response));
    }

    [Fact]
    public async Task Complexity_OnFailure_FallsBackThroughRemainingRoutesInOrder()
    {
        using var failing = Fails();
        using var ok = Ok("mid");
        var map = new Dictionary<ComplexityTier, string> { [ComplexityTier.Simple] = "small" };

        // The Simple-tier route ("small") is chosen first but fails; fallback tries the next registered route in order.
        using var client = new ComplexityRouter(
        [
            new ChatRoute("small", client: failing),
            new ChatRoute("mid", client: ok),
            new ChatRoute("big", client: ok),
        ], map);

        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("mid", SelectedRoute(response));
    }

    // ------------------------------------------------------------------------
    // CapabilityGatingClient — hard correctness filter on required features.
    // ------------------------------------------------------------------------

    [Fact]
    public void Capability_RequiredCapabilities_DerivedFromRequest()
    {
        Assert.Equal(ModelCapabilities.None, CapabilityGatingRouter.RequiredCapabilities([User("hi")], null));

        var tools = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "tool")] };
        Assert.Equal(ModelCapabilities.ToolCalling, CapabilityGatingRouter.RequiredCapabilities([User("hi")], tools));

        var json = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        Assert.Equal(ModelCapabilities.StructuredOutput, CapabilityGatingRouter.RequiredCapabilities([User("hi")], json));

        var image = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        Assert.Equal(ModelCapabilities.Vision, CapabilityGatingRouter.RequiredCapabilities([image], null));
    }

    [Fact]
    public async Task Capability_SelectsRouteThatAdvertisesRequiredCapability()
    {
        using var text = Ok("text");
        using var tools = Ok("tools");
        using var client = new CapabilityGatingRouter(
        [
            CapRoute("text", text, ModelCapabilities.None),
            CapRoute("tools", tools, ModelCapabilities.ToolCalling),
        ]);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "tool")] };
        ChatResponse response = await client.GetResponseAsync([User("call a tool")], options);

        // The text-only route is skipped because the request carries a tool; only the tool-capable route qualifies.
        Assert.Equal("tools", SelectedRoute(response));
    }

    [Fact]
    public async Task Capability_SelectsVisionRoute_ForImageRequest()
    {
        using var text = Ok("text");
        using var vision = Ok("vision");
        using var client = new CapabilityGatingRouter(
        [
            CapRoute("text", text, ModelCapabilities.None),
            CapRoute("vision", vision, ModelCapabilities.Vision),
        ]);

        var image = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([image]);

        Assert.Equal("vision", SelectedRoute(response));
    }

    [Fact]
    public async Task Capability_OnFailure_FallsBackToNextCapableRoute()
    {
        using var failing = Fails();
        using var text = Ok("text");
        using var toolsB = Ok("tools-b");
        using var client = new CapabilityGatingRouter(
        [
            CapRoute("tools-a", failing, ModelCapabilities.ToolCalling),
            CapRoute("text", text, ModelCapabilities.None),
            CapRoute("tools-b", toolsB, ModelCapabilities.ToolCalling),
        ]);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "tool")] };
        ChatResponse response = await client.GetResponseAsync([User("call a tool")], options);

        // tools-a fails; the text route can never serve a tool request, so fallback skips it to the next tool route.
        Assert.Equal("tools-b", SelectedRoute(response));
    }

    [Fact]
    public async Task Capability_WhenNoRouteCanServe_Throws()
    {
        using var ok = Ok("text");
        using var client = new CapabilityGatingRouter([CapRoute("text", ok, ModelCapabilities.None)]);

        // A tool request with no tool-capable route: the policy returns null on the first call and the base throws
        // rather than silently sending the request to an incapable route.
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "tool")] };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([User("call a tool")], options));
    }

    private static ChatRoute CapRoute(string name, IChatClient client, ModelCapabilities capabilities) =>
        new(
            name,
            client,
            additionalProperties: new AdditionalPropertiesDictionary
            {
                [CapabilityGatingRouter.CapabilitiesKey] = capabilities,
            });

    // ------------------------------------------------------------------------
    // CooldownRoutingClient — put a rate-limited route on a self-expiring cooldown.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Cooldown_OnRateLimit_FallsBackAndSkipsCooledRouteUntilItRecovers()
    {
        DateTimeOffset clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int primaryCalls = 0;
        bool primaryShouldFail = true;

        using var primary = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                primaryCalls++;
                return primaryShouldFail
                    ? throw new RateLimitException { RetryAfter = TimeSpan.FromSeconds(30) }
                    : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "primary")));
            },
        };
        using var backup = Ok("backup");

        using var client = new CooldownRouter(
            [new ChatRoute("primary", client: primary), new ChatRoute("backup", client: backup)],
            () => clock);

        // Request 1: primary rate-limits, is cooled for 30s, and the router falls back to backup.
        ChatResponse first = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("backup", SelectedRoute(first));
        Assert.Equal(1, primaryCalls);

        // Request 2 (same instant): primary is still cooling, so it is skipped entirely — backup answers without
        // primary being attempted, even though primary would now succeed.
        primaryShouldFail = false;
        ChatResponse second = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("backup", SelectedRoute(second));
        Assert.Equal(1, primaryCalls);

        // Request 3 (cooldown elapsed): primary is eligible again and answers.
        clock += TimeSpan.FromSeconds(31);
        ChatResponse third = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("primary", SelectedRoute(third));
        Assert.Equal(2, primaryCalls);
    }

    // ------------------------------------------------------------------------
    // CircuitBreakerRoutingClient — open a route's circuit after N consecutive failures.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task CircuitBreaker_OpensAfterThreshold_SkipsRoute_ThenRecoversAfterWindow()
    {
        DateTimeOffset clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int primaryCalls = 0;
        bool primaryShouldFail = true;

        using var primary = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                primaryCalls++;
                return primaryShouldFail
                    ? throw new InvalidOperationException("primary down")
                    : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "primary")));
            },
        };
        using var backup = Ok("backup");

        using var client = new CircuitBreakerRouter(
            [new ChatRoute("primary", client: primary), new ChatRoute("backup", client: backup)],
            () => clock,
            failureThreshold: 2,
            openDuration: TimeSpan.FromSeconds(30));

        // Requests 1 and 2: primary fails each time and the router falls back to backup. After the 2nd failure the
        // primary's circuit trips open.
        Assert.Equal("backup", SelectedRoute(await client.GetResponseAsync([User("hi")])));
        Assert.Equal("backup", SelectedRoute(await client.GetResponseAsync([User("hi")])));
        Assert.Equal(2, primaryCalls);

        // Request 3 (same instant): primary's circuit is open, so it is skipped without being attempted.
        primaryShouldFail = false;
        Assert.Equal("backup", SelectedRoute(await client.GetResponseAsync([User("hi")])));
        Assert.Equal(2, primaryCalls);

        // Request 4 (reset window elapsed): primary gets a half-open trial, succeeds, and its streak is cleared.
        clock += TimeSpan.FromSeconds(31);
        Assert.Equal("primary", SelectedRoute(await client.GetResponseAsync([User("hi")])));
        Assert.Equal(3, primaryCalls);
    }

    // ------------------------------------------------------------------------
    // StickyRoutingClient — app-owned pins layered over an inner policy.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Sticky_HonorsPin_OverInnerPolicy()
    {
        using var primary = Ok("primary");
        using var backup = Ok("backup");
        using var client = new StickyRouter(
            [new ChatRoute("primary", client: primary), new ChatRoute("backup", client: backup)],
            getPins: (_, _) => ["backup"],
            inner: FirstUnattempted);

        // Inner ordered-failover would pick "primary" first, but the pin forces "backup".
        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("backup", SelectedRoute(response));
    }

    [Fact]
    public async Task Sticky_StalePin_DefersToInnerPolicy()
    {
        using var primary = Ok("primary");
        using var backup = Ok("backup");
        using var client = new StickyRouter(
            [new ChatRoute("primary", client: primary), new ChatRoute("backup", client: backup)],
            getPins: (_, _) => ["ghost"], // no such registered route
            inner: FirstUnattempted);

        // A pin that no longer resolves is skipped, so the inner policy's first route answers.
        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("primary", SelectedRoute(response));
    }

    [Fact]
    public async Task Sticky_NoPin_DefersToInnerPolicy()
    {
        using var primary = Ok("primary");
        using var backup = Ok("backup");
        using var client = new StickyRouter(
            [new ChatRoute("primary", client: primary), new ChatRoute("backup", client: backup)],
            getPins: (_, _) => null,
            inner: FirstUnattempted);

        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("primary", SelectedRoute(response));
    }

    [Fact]
    public async Task Sticky_OnFailure_WalksRemainingPins()
    {
        using var failing = Fails();
        using var ok = Ok("backup");
        using var client = new StickyRouter(
            [new ChatRoute("primary", client: failing), new ChatRoute("backup", client: ok)],
            getPins: (_, _) => ["primary", "backup"],
            inner: FirstUnattempted);

        // The first pin fails; fallback walks to the next pin before deferring to the inner policy.
        ChatResponse response = await client.GetResponseAsync([User("hi")]);
        Assert.Equal("backup", SelectedRoute(response));
    }

    private static ValueTask<ChatRoute?> FirstUnattempted(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken) =>
        new(routes.FirstOrDefault(r => !attempted.Contains(r)));

    // ------------------------------------------------------------------------
    // CheapestRouteClient — cheapest route whose context window fits the prompt.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Cheapest_SelectsLowestCostRouteThatFits()
    {
        using var premium = Ok("premium");
        using var cheap = Ok("cheap");
        using var mid = Ok("mid");
        using var client = new CheapestRouter(
        [
            EconomicRoute("premium", premium, 20m),
            EconomicRoute("cheap", cheap, 1m),
            EconomicRoute("mid", mid, 5m),
        ]);

        ChatResponse response = await client.GetResponseAsync([User("hello")]);
        Assert.Equal("cheap", SelectedRoute(response));
    }

    [Fact]
    public async Task Cheapest_SkipsRoutesWhoseContextWindowIsTooSmall()
    {
        using var cheapSmall = Ok("cheap-small");
        using var midLarge = Ok("mid-large");
        using var client = new CheapestRouter(
        [
            EconomicRoute("cheap-small", cheapSmall, 1m, 5),
            EconomicRoute("mid-large", midLarge, 5m, 100_000),
        ]);

        // A long prompt (~50 estimated tokens) does not fit the cheapest route's 5-token window, so the next cheapest
        // route that does fit is chosen.
        ChatResponse response = await client.GetResponseAsync([User(new string('x', 200))]);
        Assert.Equal("mid-large", SelectedRoute(response));
    }

    [Fact]
    public async Task Cheapest_OnFailure_FallsBackToNextCheapest()
    {
        using var failing = Fails();
        using var ok = Ok("mid");
        using var client = new CheapestRouter(
        [
            EconomicRoute("cheap", failing, 1m),
            EconomicRoute("mid", ok, 5m),
        ]);

        ChatResponse response = await client.GetResponseAsync([User("hello")]);
        Assert.Equal("mid", SelectedRoute(response));
    }

    [Fact]
    public async Task Cheapest_Streaming_FallsBackToNextCheapest()
    {
        using var failing = StreamThrows();
        using var working = StreamsOk("mid");
        using var client = new CheapestRouter(
        [
            EconomicRoute("cheap", failing, 1m),
            EconomicRoute("mid", working, 5m),
        ]);

        ChatResponseUpdate? first = null;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([User("hello")]))
        {
            first ??= update;
        }

        Assert.NotNull(first);
        Assert.Equal("mid", first!.Text);
    }

    private static ChatRoute EconomicRoute(
        string name,
        IChatClient client,
        decimal inputTokenCostPerMillion,
        int? maxInputTokens = null) =>
        new(
            name,
            client,
            additionalProperties: new AdditionalPropertiesDictionary
            {
                [CheapestRouter.EconomicsKey] = new RouteEconomics(inputTokenCostPerMillion, maxInputTokens),
            });

    // ------------------------------------------------------------------------
    // SemanticRoutingClient — embedding-similarity routing (offline fake embeddings).
    // ------------------------------------------------------------------------

    // Canned unit vectors so cosine similarity is deterministic: code-like text -> (1,0,0), chat-like text -> (0,1,0),
    // anything unknown -> (0,0,1) which is orthogonal to both profiles and therefore below the similarity threshold.
    private static readonly Dictionary<string, float[]> _cannedVectors = new(StringComparer.Ordinal)
    {
        ["sort a list in code"] = [1f, 0f, 0f],
        ["how do I sort a list in code"] = [1f, 0f, 0f],
        ["hello there"] = [0f, 1f, 0f],
        ["say hello there"] = [0f, 1f, 0f],
    };

    private static TestEmbeddingGenerator CannedEmbeddings() => new()
    {
        GenerateAsyncCallback = (values, _, _) =>
        {
            var embeddings = new List<Embedding<float>>();
            foreach (string value in values)
            {
                float[] vector = _cannedVectors.TryGetValue(value, out float[]? v) ? v : [0f, 0f, 1f];
                embeddings.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        },
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> SemanticProfiles() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["code"] = ["sort a list in code"],
            ["chat"] = ["hello there"],
        };

    [Fact]
    public async Task Semantic_RoutesToMostSimilarRoute()
    {
        using var code = Ok("code");
        using var chat = Ok("chat");
        using var embeddings = CannedEmbeddings();
        using var client = new SemanticRouter(
            [new ChatRoute("code", client: code), new ChatRoute("chat", client: chat)],
            embeddings, SemanticProfiles(), defaultRoute: "chat");

        // A code-like query embeds nearest the "code" profile utterances.
        ChatResponse response = await client.GetResponseAsync([User("how do I sort a list in code")]);
        Assert.Equal("code", SelectedRoute(response));
    }

    [Fact]
    public async Task Semantic_BelowThreshold_UsesDefaultRoute()
    {
        using var code = Ok("code");
        using var chat = Ok("chat");
        using var embeddings = CannedEmbeddings();
        using var client = new SemanticRouter(
            [new ChatRoute("code", client: code), new ChatRoute("chat", client: chat)],
            embeddings, SemanticProfiles(), defaultRoute: "chat");

        // An unrelated query is orthogonal to every profile and clears no route's threshold, so the default answers.
        ChatResponse response = await client.GetResponseAsync([User("totally unrelated gibberish")]);
        Assert.Equal("chat", SelectedRoute(response));
    }

    [Fact]
    public async Task Semantic_OnFailure_FallsBackInRegistrationOrder()
    {
        using var failing = Fails();
        using var ok = Ok("chat");
        using var embeddings = CannedEmbeddings();
        using var client = new SemanticRouter(
            [new ChatRoute("code", client: failing), new ChatRoute("chat", client: ok)],
            embeddings, SemanticProfiles(), defaultRoute: "chat");

        // "code" wins the semantic match but fails; fallback tries the next registered route in order.
        ChatResponse response = await client.GetResponseAsync([User("how do I sort a list in code")]);
        Assert.Equal("chat", SelectedRoute(response));
    }

    // ========================================================================
    // Inlined policy subclasses (trimmed, faithful copies of docs/samples/routing/*.cs).
    // ========================================================================

    public enum ComplexityTier
    {
        Simple,
        Medium,
        Complex,
        Reasoning,
    }

    // docs/samples/routing/ComplexityRoutingClient.cs
    private sealed class ComplexityRouter : RoutingChatClient
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

        public ComplexityRouter(
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyDictionary<ComplexityTier, string> routeByTier,
            string? defaultRoute = null)
            : base(routes)
        {
            _routeByTier = routeByTier;
            _defaultRoute = defaultRoute;
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            if (attempted.Count > 0)
            {
                return new(routes.Except(attempted).FirstOrDefault());
            }

            ComplexityTier tier = Classify(messages);
            string? targetName = _routeByTier.TryGetValue(tier, out string? mapped) ? mapped : _defaultRoute;

            ChatRoute? target = targetName is null
                ? null
                : routes.FirstOrDefault(r => string.Equals(r.Name, targetName, StringComparison.OrdinalIgnoreCase));

            return new(target ?? routes[0]);
        }

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

    [Flags]
    public enum ModelCapabilities
    {
        None = 0,
        ToolCalling = 1 << 0,
        Vision = 1 << 1,
        StructuredOutput = 1 << 2,
    }

    // docs/samples/routing/CapabilityGatingClient.cs
    private sealed class CapabilityGatingRouter : RoutingChatClient
    {
        public const string CapabilitiesKey = "capabilities";

        public CapabilityGatingRouter(IReadOnlyList<ChatRoute> routes)
            : base(routes)
        {
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            ModelCapabilities required = RequiredCapabilities(messages, options);
            return new(routes.Except(attempted).FirstOrDefault(r => Supports(r, required)));
        }

        public static ModelCapabilities RequiredCapabilities(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            ModelCapabilities required = ModelCapabilities.None;

            if (options?.Tools is { Count: > 0 })
            {
                required |= ModelCapabilities.ToolCalling;
            }

            if (options?.ResponseFormat is ChatResponseFormatJson)
            {
                required |= ModelCapabilities.StructuredOutput;
            }

            if (HasImageContent(messages))
            {
                required |= ModelCapabilities.Vision;
            }

            return required;
        }

        private static bool HasImageContent(IEnumerable<ChatMessage> messages)
        {
            foreach (ChatMessage message in messages)
            {
                foreach (AIContent content in message.Contents)
                {
                    bool isImage = content switch
                    {
                        DataContent data => data.HasTopLevelMediaType("image"),
                        UriContent uri => uri.HasTopLevelMediaType("image"),
                        _ => false,
                    };

                    if (isImage)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Supports(ChatRoute route, ModelCapabilities required)
        {
            ModelCapabilities available =
                route.AdditionalProperties?.TryGetValue(CapabilitiesKey, out ModelCapabilities capabilities) == true
                    ? capabilities
                    : ModelCapabilities.None;
            return (available & required) == required;
        }
    }

    private sealed class RateLimitException : Exception
    {
        public TimeSpan? RetryAfter { get; init; }
    }

    // docs/samples/routing/CooldownRoutingClient.cs (trimmed: reads Retry-After off a test exception and takes an
    // injectable clock instead of DateTimeOffset.UtcNow so recovery can be tested without waiting).
    private sealed class CooldownRouter : RoutingChatClient
    {
        private static readonly TimeSpan _defaultCooldown = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _coolUntil = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> _now;

        public CooldownRouter(IReadOnlyList<ChatRoute> routes, Func<DateTimeOffset> now)
            : base(routes)
        {
            _now = now;
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            if (lastException is not null && attempted.Count > 0)
            {
                ChatRoute failed = attempted[attempted.Count - 1];
                _coolUntil[failed.Name] = _now() + (RetryAfter(lastException) ?? _defaultCooldown);
            }

            DateTimeOffset now = _now();

            ChatRoute? next = routes
                .Except(attempted)
                .FirstOrDefault(r => !IsCooling(r.Name, now));

            next ??= routes
                .Except(attempted)
                .OrderBy(r => _coolUntil.TryGetValue(r.Name, out DateTimeOffset until) ? until : DateTimeOffset.MinValue)
                .FirstOrDefault();

            return new(next);
        }

        private bool IsCooling(string routeName, DateTimeOffset now) =>
            _coolUntil.TryGetValue(routeName, out DateTimeOffset until) && until > now;

        private static TimeSpan? RetryAfter(Exception exception) =>
            exception is RateLimitException { RetryAfter: { } retryAfter } ? retryAfter : null;
    }

    // docs/samples/routing/CircuitBreakerRoutingClient.cs (trimmed: injectable clock, threshold, and window).
    private sealed class CircuitBreakerRouter : RoutingChatClient
    {
        private readonly ConcurrentDictionary<string, Breaker> _breakers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> _now;
        private readonly int _failureThreshold;
        private readonly TimeSpan _openDuration;

        public CircuitBreakerRouter(
            IReadOnlyList<ChatRoute> routes,
            Func<DateTimeOffset> now,
            int failureThreshold,
            TimeSpan openDuration)
            : base(routes)
        {
            _now = now;
            _failureThreshold = failureThreshold;
            _openDuration = openDuration;
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            DateTimeOffset now = _now();

            if (lastException is not null && attempted.Count > 0)
            {
                string failedName = attempted[attempted.Count - 1].Name;
                _breakers.AddOrUpdate(
                    failedName,
                    _ => new Breaker(1, now + _openDuration),
                    (_, existing) =>
                    {
                        int failures = existing.Failures + 1;
                        DateTimeOffset openUntil = failures >= _failureThreshold ? now + _openDuration : existing.OpenUntil;
                        return new Breaker(failures, openUntil);
                    });
            }

            ChatRoute? next = routes
                .Except(attempted)
                .FirstOrDefault(r => !IsOpen(r.Name, now));

            // Clear the chosen route's streak only when it is a half-open trial (its open window has elapsed), so one
            // stale failure cannot immediately re-trip it. A route still accumulating failures below the threshold
            // keeps its streak, so consecutive failures add up and actually open the circuit.
            if (next is not null && IsHalfOpen(next.Name, now))
            {
                _ = _breakers.TryRemove(next.Name, out _);
            }

            return new(next);
        }

        private bool IsOpen(string routeName, DateTimeOffset now) =>
            _breakers.TryGetValue(routeName, out Breaker breaker) &&
            breaker.Failures >= _failureThreshold &&
            breaker.OpenUntil > now;

        private bool IsHalfOpen(string routeName, DateTimeOffset now) =>
            _breakers.TryGetValue(routeName, out Breaker breaker) &&
            breaker.Failures >= _failureThreshold &&
            breaker.OpenUntil <= now;

        private readonly record struct Breaker(int Failures, DateTimeOffset OpenUntil);
    }

    // docs/samples/routing/StickyRoutingClient.cs
    private sealed class StickyRouter : RoutingChatClient
    {
        public delegate ValueTask<ChatRoute?> InnerSelector(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken);

        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<string>?> _getPins;
        private readonly InnerSelector _inner;

        public StickyRouter(
            IReadOnlyList<ChatRoute> routes,
            Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<string>?> getPins,
            InnerSelector inner)
            : base(routes)
        {
            _getPins = getPins;
            _inner = inner;
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string>? pins = _getPins(messages, options);
            if (pins is { Count: > 0 })
            {
                ChatRoute? pinned = pins
                    .Select(name => routes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(r => r is not null && !attempted.Contains(r));

                if (pinned is not null)
                {
                    return new(pinned);
                }
            }

            return _inner(messages, options, routes, attempted, lastException, cancellationToken);
        }
    }

    private readonly record struct RouteEconomics(decimal InputTokenCostPerMillion, int? MaxInputTokens = null);

    // docs/samples/routing/CheapestRouteClient.cs
    private sealed class CheapestRouter : RoutingChatClient
    {
        public const string EconomicsKey = "economics";
        private const int CharactersPerToken = 4;

        public CheapestRouter(IReadOnlyList<ChatRoute> routes)
            : base(routes)
        {
        }

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            int approxTokens = messages.Sum(m => m.Text.Length) / CharactersPerToken;

            ChatRoute? next = routes
                .Except(attempted)
                .Select(r => (Route: r, Economics: GetEconomics(r)))
                .Where(candidate =>
                    candidate.Economics.MaxInputTokens is null ||
                    candidate.Economics.MaxInputTokens >= approxTokens)
                .OrderBy(candidate => candidate.Economics.InputTokenCostPerMillion)
                .Select(candidate => candidate.Route)
                .FirstOrDefault();

            return new(next);
        }

        private static RouteEconomics GetEconomics(ChatRoute route) =>
            route.AdditionalProperties?.TryGetValue(EconomicsKey, out RouteEconomics economics) == true
                ? economics
                : new(decimal.MaxValue);
    }

    // docs/samples/routing/SemanticRoutingClient.cs (trimmed: manual cosine similarity instead of TensorPrimitives,
    // and a simple lazy index without the semaphore — the tests are single-threaded).
    private sealed class SemanticRouter : RoutingChatClient
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
        private readonly IReadOnlyDictionary<string, string[]> _profiles;
        private readonly string? _defaultRoute;
        private readonly int _topK;
        private readonly float _scoreThreshold;
        private (string route, float[] vector)[]? _index;

        public SemanticRouter(
            IReadOnlyList<ChatRoute> routes,
            IEmbeddingGenerator<string, Embedding<float>> embeddings,
            IReadOnlyDictionary<string, IReadOnlyList<string>> routeProfiles,
            string? defaultRoute = null,
            int topK = 5,
            float scoreThreshold = 0.3f)
            : base(routes)
        {
            _embeddings = embeddings;
            _profiles = routeProfiles.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            _defaultRoute = defaultRoute;
            _topK = topK;
            _scoreThreshold = scoreThreshold;
        }

        protected override async ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken)
        {
            if (attempted.Count > 0)
            {
                return routes.Except(attempted).FirstOrDefault();
            }

            string? query = LastUserText(messages);
            if (string.IsNullOrWhiteSpace(query))
            {
                return DefaultRoute(routes);
            }

            (string route, float[] vector)[] index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

            GeneratedEmbeddings<Embedding<float>> queryEmbedding =
                await _embeddings.GenerateAsync([query!], cancellationToken: cancellationToken).ConfigureAwait(false);
            float[] queryVector = queryEmbedding[0].Vector.ToArray();

            var registered = routes.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<(string route, double score)> matches = index
                .Where(entry => registered.Contains(entry.route))
                .Select(entry => (entry.route, score: (double)Cosine(queryVector, entry.vector)))
                .OrderByDescending(m => m.score)
                .Take(_topK)
                .ToList();

            string? winner = matches
                .GroupBy(m => m.route, StringComparer.OrdinalIgnoreCase)
                .Select(g => (route: g.Key, score: g.Average(m => m.score)))
                .Where(g => g.score >= _scoreThreshold)
                .OrderByDescending(g => g.score)
                .Select(g => g.route)
                .FirstOrDefault();

            return winner is null
                ? DefaultRoute(routes)
                : routes.FirstOrDefault(r => string.Equals(r.Name, winner, StringComparison.OrdinalIgnoreCase)) ?? DefaultRoute(routes);
        }

        private ChatRoute DefaultRoute(IReadOnlyList<ChatRoute> routes) =>
            (_defaultRoute is null ? null : routes.FirstOrDefault(r => string.Equals(r.Name, _defaultRoute, StringComparison.OrdinalIgnoreCase)))
            ?? routes[0];

        private static string? LastUserText(IEnumerable<ChatMessage> messages)
        {
            string? last = null;
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.User)
                {
                    last = message.Text;
                }
            }

            return last;
        }

        private async ValueTask<(string route, float[] vector)[]> EnsureIndexAsync(CancellationToken cancellationToken)
        {
            if (_index is { } cached)
            {
                return cached;
            }

            var routeNames = new List<string>();
            var utterances = new List<string>();
            foreach (KeyValuePair<string, string[]> profile in _profiles)
            {
                foreach (string utterance in profile.Value)
                {
                    routeNames.Add(profile.Key);
                    utterances.Add(utterance);
                }
            }

            GeneratedEmbeddings<Embedding<float>> embeddings =
                await _embeddings.GenerateAsync(utterances, cancellationToken: cancellationToken).ConfigureAwait(false);

            var index = new (string route, float[] vector)[utterances.Count];
            for (int i = 0; i < utterances.Count; i++)
            {
                index[i] = (routeNames[i], embeddings[i].Vector.ToArray());
            }

            _index = index;
            return index;
        }

        private static float Cosine(float[] a, float[] b)
        {
            double dot = 0;
            double na = 0;
            double nb = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }

            return na <= 0 || nb <= 0 ? 0f : (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
        }
    }
}
