// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class RoutingChatClientTests
{
    [Fact]
    public void Constructor_RejectsEmptyModels()
    {
        Assert.Throws<ArgumentException>(() => new RoutingChatClient([]));
    }

    [Fact]
    public void Constructor_RejectsModelWithoutClient()
    {
        Assert.Throws<ArgumentException>(() => new RoutingChatClient([new RoutingChatModel("m1")]));
    }

    [Fact]
    public async Task DefaultSelector_ForwardsModelId_StampsResponse_AndDoesNotLeakIntoRequest()
    {
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var options = new ChatOptions();
        ChatOptions? forwarded = null;

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (m, o, _) =>
            {
                Assert.Same(messages, m);
                forwarded = o;
                return Task.FromResult(expectedResponse);
            }
        };

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m1", providerName: "openai", modelId: "gpt-4o-mini", client: inner)]);

        ChatResponse response = await client.GetResponseAsync(messages, options, CancellationToken.None);

        Assert.Same(expectedResponse, response);

        // The provider model id is forwarded (the caller did not pin one), but on a clone.
        Assert.NotNull(forwarded);
        Assert.NotSame(options, forwarded);
        Assert.Equal("gpt-4o-mini", forwarded!.ModelId);

        // Routing internals are never written into the forwarded request.
        Assert.True(forwarded.AdditionalProperties is null || forwarded.AdditionalProperties.Count == 0);

        // The decision is stamped on the response.
        Assert.NotNull(response.AdditionalProperties);
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
        Assert.Equal("gpt-4o-mini", response.AdditionalProperties[RoutingChatClient.SelectedModelIdKey]);
        Assert.Equal("openai", response.AdditionalProperties[RoutingChatClient.SelectedProviderNameKey]);
    }

    [Fact]
    public async Task DefaultSelector_HonorsExplicitModelId()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new RoutingChatClient(
        [
            new RoutingChatModel("m1", modelId: "gpt-a", client: c1),
            new RoutingChatModel("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-b" });

        Assert.Same(r2, response);
        Assert.Equal("m2", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public async Task PerInstanceStickiness_ReusesFirstSelectedModel()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        int selectionCount = 0;
        IChatRouteSelector selector = ChatRouteSelector.Create(
            ctx => new ChatRoutePlan(ctx.Models[selectionCount++ % ctx.Models.Count]));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m1", client: c1), new RoutingChatModel("m2", client: c2)],
            selector,
            RoutingStickiness.PerInstance);

        ChatResponse first = await client.GetResponseAsync([new(ChatRole.User, "first")], new ChatOptions { ConversationId = "c1" });
        ChatResponse second = await client.GetResponseAsync([new(ChatRole.User, "second")], new ChatOptions { ConversationId = "c2" });

        Assert.Same(r1, first);
        Assert.Same(r1, second);
        Assert.Equal(1, selectionCount);
    }

    [Fact]
    public async Task ByConversationIdStickiness_SticksPerConversation()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        int selectionCount = 0;
        IChatRouteSelector selector = ChatRouteSelector.Create(
            ctx => new ChatRoutePlan(ctx.Models[selectionCount++ % ctx.Models.Count]));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m1", client: c1), new RoutingChatModel("m2", client: c2)],
            selector,
            RoutingStickiness.ByConversationId);

        ChatResponse c1First = await client.GetResponseAsync([new(ChatRole.User, "turn1")], new ChatOptions { ConversationId = "c1" });
        ChatResponse c1Second = await client.GetResponseAsync([new(ChatRole.User, "turn2")], new ChatOptions { ConversationId = "c1" });
        ChatResponse c2First = await client.GetResponseAsync([new(ChatRole.User, "turn3")], new ChatOptions { ConversationId = "c2" });

        Assert.Same(r1, c1First);
        Assert.Same(r1, c1Second);
        Assert.Same(r2, c2First);
    }

    [Fact]
    public async Task ByConversationIdStickiness_WithoutConversationIdFallsBackToEveryCall()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        int selectionCount = 0;
        IChatRouteSelector selector = ChatRouteSelector.Create(
            ctx => new ChatRoutePlan(ctx.Models[selectionCount++ % ctx.Models.Count]));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m1", client: c1), new RoutingChatModel("m2", client: c2)],
            selector,
            RoutingStickiness.ByConversationId);

        ChatResponse first = await client.GetResponseAsync([new(ChatRole.User, "turn1")]);
        ChatResponse second = await client.GetResponseAsync([new(ChatRole.User, "turn2")], new ChatOptions { ConversationId = "" });

        Assert.Same(r1, first);
        Assert.Same(r2, second);
    }

    [Fact]
    public async Task RemainsValid_StickyHitIsReused_AndReselectsWhenInvalidated()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        bool valid = true;
        int selectionCount = 0;
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx =>
        {
            RoutingChatModel model = ctx.Models[selectionCount++ % ctx.Models.Count];
            return new ChatRoutePlan(model, (_, _) => new ValueTask<bool>(valid));
        });

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m1", client: c1), new RoutingChatModel("m2", client: c2)],
            selector,
            RoutingStickiness.PerInstance);

        ChatResponse first = await client.GetResponseAsync([new(ChatRole.User, "a")]);
        ChatResponse second = await client.GetResponseAsync([new(ChatRole.User, "b")]);

        Assert.Same(r1, first);
        Assert.Same(r1, second);
        Assert.Equal(1, selectionCount);

        valid = false;
        ChatResponse third = await client.GetResponseAsync([new(ChatRole.User, "c")]);

        Assert.Same(r2, third);
        Assert.Equal(2, selectionCount);
    }

    [Fact]
    public async Task Fallback_WalksPlanOnFailure_AndStampsTheModelThatSucceeded()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));

        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("primary", client: failing), new RoutingChatModel("fallback", client: working)],
            selector);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public async Task Fallback_Exhausted_PropagatesLastException()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("a", client: failing1), new RoutingChatModel("b", client: failing2)],
            selector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal("b", ex.Message);
    }

    [Fact]
    public async Task Fallback_DisabledByDefault_SingleModelPlanDoesNotTryOtherModels()
    {
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("primary") };
        bool otherCalled = false;
        using var other = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                otherCalled = true;
                return Task.FromResult(new ChatResponse());
            },
        };

        // The selector picks a single model and no router fallback is configured, so the failure surfaces.
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models[0]));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("primary", client: failing), new RoutingChatModel("other", client: other)],
            selector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal("primary", ex.Message);
        Assert.False(otherCalled);
    }

    [Fact]
    public async Task Fallback_RouterOwnedPolicy_TriesRemainingModelsInRegistrationOrder()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        // The selector picks a single model; the router owns fallback over the remaining models.
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models[0]));

        using RoutingChatClient client = new RoutingChatClientBuilder()
            .AddModel("primary", failing)
            .AddModel("fallback", working)
            .UseSelector(selector)
            .UseFallback()
            .Build();

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public async Task Fallback_CustomPolicy_ControlsTailOrder()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("primary") };
        bool aCalled = false;
        using var a = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                aCalled = true;
                throw new InvalidOperationException("a");
            },
        };
        using var b = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models[0]));

        using var client = new RoutingChatClient(
            [
                new RoutingChatModel("primary", client: failing),
                new RoutingChatModel("a", client: a),
                new RoutingChatModel("b", client: b),
            ],
            selector,
            fallback: (_, remaining) =>
            {
                // Reverse registration order so "b" is tried before "a".
                var reversed = new List<RoutingChatModel>(remaining);
                reversed.Reverse();
                return reversed;
            });

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("b", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
        Assert.False(aCalled);
    }

    [Fact]
    public async Task SelectorRoutingToUnregisteredModel_Throws()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        var stranger = new RoutingChatModel("stranger", client: inner);
        IChatRouteSelector selector = ChatRouteSelector.Create(_ => new ChatRoutePlan(stranger));

        using var client = new RoutingChatClient([new RoutingChatModel("m1", client: inner)], selector);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task Streaming_StampsOnlyTheFirstUpdate()
    {
        using var inner = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("a", "b") };

        using var client = new RoutingChatClient([new RoutingChatModel("m1", modelId: "x", client: inner)]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("m1", updates[0].AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
        Assert.True(
            updates[1].AdditionalProperties is null ||
            !updates[1].AdditionalProperties!.ContainsKey(RoutingChatClient.SelectedModelNameKey));
    }

    [Fact]
    public async Task Streaming_FallsBackBeforeFirstUpdate()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));

        using var client = new RoutingChatClient(
            [new RoutingChatModel("primary", client: failing), new RoutingChatModel("fallback", client: working)],
            selector);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("fallback", updates[0].AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public void GetService_ReturnsClientAndSelector()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };
        IChatRouteSelector selector = RuleBasedChatRouteSelector.Instance;

        using var client = new RoutingChatClient([new RoutingChatModel("m1", client: inner)], selector);

        Assert.Same(client, client.GetService(typeof(RoutingChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(selector, client.GetService(typeof(IChatRouteSelector)));
        Assert.Same(selector, client.GetService(typeof(RuleBasedChatRouteSelector)));
        Assert.Null(client.GetService(typeof(string)));
    }

    [Fact]
    public void Dispose_DisposesEachClientExactlyOnce()
    {
        // The RoutingChatClient takes ownership and disposes the candidate clients; that is what is under test.
#pragma warning disable CA2000 // Dispose objects before losing scope
        var shared = new CountingDisposeClient();
        var other = new CountingDisposeClient();

        var client = new RoutingChatClient(
        [
            new RoutingChatModel("a", client: shared),
            new RoutingChatModel("b", client: shared),
            new RoutingChatModel("c", client: other),
        ]);
#pragma warning restore CA2000

        client.Dispose();

        Assert.Equal(1, shared.DisposeCount);
        Assert.Equal(1, other.DisposeCount);
    }

    [Fact]
    public async Task Builder_BuildsRoutingClient()
    {
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(expected) };

        using RoutingChatClient client = new RoutingChatClientBuilder()
            .AddModel("m1", inner, modelId: "x")
            .UseStickiness(RoutingStickiness.PerInstance)
            .Build();

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(expected, response);
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public void Builder_RejectsDuplicateModelNames()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        var builder = new RoutingChatClientBuilder().AddModel("dup", inner);

        Assert.Throws<ArgumentException>(() => builder.AddModel("dup", inner));
    }

    [Fact]
    public async Task RuleBasedSelector_PrefersLowerCostWithoutSignal()
    {
        var models = new[]
        {
            new RoutingChatModel("expensive", inputTokenCostPerMillion: 10m, outputTokenCostPerMillion: 10m),
            new RoutingChatModel("cheap", inputTokenCostPerMillion: 1m, outputTokenCostPerMillion: 1m),
        };

        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, models);
        ChatRoutePlan plan = await RuleBasedChatRouteSelector.Instance.SelectRouteAsync(context);

        Assert.Equal("cheap", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task RuleBasedSelector_PrefersToolCallingModelWhenToolsRequested()
    {
        var models = new[]
        {
            new RoutingChatModel("plain"),
            new RoutingChatModel("tools", traits: RoutingChatModelTraits.ToolCalling),
        };

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "get_status")] };
        var context = new ChatRouteContext([new(ChatRole.User, "what is the status")], options, models);
        ChatRoutePlan plan = await RuleBasedChatRouteSelector.Instance.SelectRouteAsync(context);

        Assert.Equal("tools", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task RuleBasedSelector_ExtraTraitsAreNotAQualitySignal()
    {
        // With no capability required by the request, a model advertising extra traits must not
        // outrank a cheaper model: traits are a capability gate, not a quality/performance signal.
        var models = new[]
        {
            new RoutingChatModel(
                "rich",
                inputTokenCostPerMillion: 10m,
                outputTokenCostPerMillion: 10m,
                traits: RoutingChatModelTraits.ToolCalling | RoutingChatModelTraits.Vision | RoutingChatModelTraits.Reasoning),
            new RoutingChatModel("cheap", inputTokenCostPerMillion: 1m, outputTokenCostPerMillion: 1m),
        };

        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, models);
        ChatRoutePlan plan = await RuleBasedChatRouteSelector.Instance.SelectRouteAsync(context);

        Assert.Equal("cheap", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task RuleBasedSelector_RanksModelMissingRequiredTraitLastEvenWhenCheaper()
    {
        // The capability gate is hard: a cheaper model that cannot satisfy a required capability is
        // ranked behind a pricier model that can, so it is only ever a last-resort fallback.
        var models = new[]
        {
            new RoutingChatModel("cheap-no-tools", inputTokenCostPerMillion: 1m, outputTokenCostPerMillion: 1m),
            new RoutingChatModel(
                "pricey-tools",
                inputTokenCostPerMillion: 10m,
                outputTokenCostPerMillion: 10m,
                traits: RoutingChatModelTraits.ToolCalling),
        };

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "get_status")] };
        var context = new ChatRouteContext([new(ChatRole.User, "what is the status")], options, models);
        ChatRoutePlan plan = await RuleBasedChatRouteSelector.Instance.SelectRouteAsync(context);

        Assert.Equal("pricey-tools", plan.OrderedModels[0].Name);
        Assert.Equal("cheap-no-tools", plan.OrderedModels[^1].Name);
    }

    [Fact]
    public async Task SemanticSelector_RoutesToMostSimilarModel()
    {
        var models = new[]
        {
            new RoutingChatModel("code"),
            new RoutingChatModel("weather"),
        };

        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["weather"] = ["what is the weather like"],
            ["code"] = ["fix this bug in the code"],
        };

        using var embedder = KeywordEmbeddingGenerator();
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "is it going to rain today")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("weather", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_FallsBackToFirstModelBelowMinimumSimilarity()
    {
        var models = new[]
        {
            new RoutingChatModel("code"),
            new RoutingChatModel("weather"),
        };

        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["weather"] = ["what is the weather like"],
            ["code"] = ["fix this bug in the code"],
        };

        using var embedder = KeywordEmbeddingGenerator();
        var selector = new SemanticChatRouteSelector(
            embedder, profiles, options: new SemanticRouterOptions { ScoreThreshold = 0.99f });
        var context = new ChatRouteContext([new(ChatRole.User, "is it going to rain today")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        // "rain" is only weakly similar to the weather profile (below 0.99), so the first registered model wins.
        Assert.Equal("code", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_AggregatesWithMeanByDefault()
    {
        // "alpha" has one near-perfect utterance and one poor one (mean 0.50); "beta" has two
        // consistently strong utterances (mean 0.70). Mean aggregation (the default) prefers "beta".
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a-high", "a-low"],
            ["beta"] = ["b-mid1", "b-mid2"],
        };

        using var embedder = VectorEmbeddingGenerator(MeanVsMaxVectors());
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("beta", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_AggregatesWithMaxWhenConfigured()
    {
        // Same profiles as the mean test, but Max aggregation prefers "alpha" for its single best (0.95).
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a-high", "a-low"],
            ["beta"] = ["b-mid1", "b-mid2"],
        };

        using var embedder = VectorEmbeddingGenerator(MeanVsMaxVectors());
        var selector = new SemanticChatRouteSelector(
            embedder, profiles, options: new SemanticRouterOptions { Aggregation = SemanticRouteAggregation.Max });
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("alpha", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_AggregatesWithSumWhenConfigured()
    {
        // "alpha" has two moderate utterances (sum 0.80); "beta" has one strong one (sum 0.70). Sum
        // aggregation prefers "alpha" even though its mean (0.40) is lower than beta's (0.70).
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["s-a1", "s-a2"],
            ["beta"] = ["s-b1"],
        };

        var vectors = new Dictionary<string, float[]>
        {
            ["route me"] = [1f, 0f],
            ["s-a1"] = [0.4f, 0.91651514f],
            ["s-a2"] = [0.4f, 0.91651514f],
            ["s-b1"] = [0.7f, 0.71414284f],
        };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(
            embedder, profiles, options: new SemanticRouterOptions { Aggregation = SemanticRouteAggregation.Sum });
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("alpha", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_AppliesGlobalTopK()
    {
        // With full mean, "alpha" (0.6) beats "beta" (mean of 0.9 and 0.1 = 0.5). But TopK=1 keeps only
        // the single best match globally (beta's 0.9), so "beta" wins — proving top-k is global.
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a1"],
            ["beta"] = ["b1", "b2"],
        };

        var vectors = new Dictionary<string, float[]>
        {
            ["route me"] = [1f, 0f],
            ["a1"] = [0.6f, 0.8f],
            ["b1"] = [0.9f, 0.43588989f],
            ["b2"] = [0.1f, 0.99498744f],
        };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(
            embedder, profiles, options: new SemanticRouterOptions { TopK = 1 });
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("beta", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_HonorsPerModelThreshold()
    {
        // "beta" scores highest (0.9) but its per-model threshold (0.95) rejects it, so the next model
        // past its threshold, "alpha" (0.6 >= global 0.3), is selected instead.
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a1"],
            ["beta"] = ["b1"],
        };

        var vectors = new Dictionary<string, float[]>
        {
            ["route me"] = [1f, 0f],
            ["a1"] = [0.6f, 0.8f],
            ["b1"] = [0.9f, 0.43588989f],
        };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(
            embedder,
            profiles,
            options: new SemanticRouterOptions
            {
                ScoreThresholdByModel = new Dictionary<string, float> { ["beta"] = 0.95f },
            });
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("alpha", plan.OrderedModels[0].Name);
    }

    [Fact]
    public async Task SemanticSelector_RoutesToDefaultModelWithoutUserText()
    {
        var models = new[] { new RoutingChatModel("alpha"), new RoutingChatModel("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a1"],
            ["beta"] = ["b1"],
        };

        using var embedder = KeywordEmbeddingGenerator();
        var selector = new SemanticChatRouteSelector(embedder, profiles, defaultModel: "beta");
        var context = new ChatRouteContext([new(ChatRole.System, "you are a helpful assistant")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        // No user message means no routing signal, so the configured default model is the primary route.
        Assert.Equal("beta", plan.OrderedModels[0].Name);
    }

    private static TestEmbeddingGenerator KeywordEmbeddingGenerator() => new()
    {
        GenerateAsyncCallback = (values, _, _) =>
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (string value in values)
            {
                result.Add(new Embedding<float>(KeywordVector(value)));
            }

            return Task.FromResult(result);
        }
    };

    // An embedder that returns an explicit, pre-computed vector for each exact input string, so tests
    // can assert on precise cosine similarities.
    private static TestEmbeddingGenerator VectorEmbeddingGenerator(IReadOnlyDictionary<string, float[]> vectors) => new()
    {
        GenerateAsyncCallback = (values, _, _) =>
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (string value in values)
            {
                result.Add(new Embedding<float>(vectors[value]));
            }

            return Task.FromResult(result);
        }
    };

    // Unit vectors whose cosine similarity with the query [1, 0] is the value encoded in the name:
    // a-high=0.95, a-low=0.05 (alpha mean 0.50, max 0.95); b-mid=0.70 (beta mean 0.70, max 0.70).
    private static Dictionary<string, float[]> MeanVsMaxVectors() => new()
    {
        ["route me"] = [1f, 0f],
        ["a-high"] = [0.95f, 0.31224990f],
        ["a-low"] = [0.05f, 0.99874922f],
        ["b-mid1"] = [0.70f, 0.71414284f],
        ["b-mid2"] = [0.70f, 0.71414284f],
    };

    private static float[] KeywordVector(string text)
    {
        text = text.ToLowerInvariant();
        if (text.Contains("rain"))
        {
            return [0.8f, 0.2f];
        }

        if (text.Contains("weather"))
        {
            return [1f, 0f];
        }

        if (text.Contains("bug") || text.Contains("code") || text.Contains("fix"))
        {
            return [0f, 1f];
        }

        return [0.5f, 0.5f];
    }

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

    private sealed class CountingDisposeClient : IChatClient
    {
        public int DisposeCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => DisposeCount++;
    }
}
