// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    public async Task AttemptEvents_RecordFallbackThenSuccess()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));
        using var client = new RoutingChatClient(
            [new RoutingChatModel("primary", modelId: "p", providerName: "prov", client: failing), new RoutingChatModel("fallback", client: working)],
            selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        Assert.Equal(2, capture.Events.Count);

        ActivityEvent first = capture.Events[0];
        Assert.Equal(RoutingChatClient.AttemptEventName, first.Name);
        Assert.Equal(1, GetTag(first, "routing.attempt.ordinal"));
        Assert.Equal("primary", GetTag(first, "routing.attempt.model"));
        Assert.Equal("p", GetTag(first, "routing.attempt.model_id"));
        Assert.Equal("prov", GetTag(first, "routing.attempt.provider"));
        Assert.Equal("fallback", GetTag(first, "routing.attempt.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, GetTag(first, "routing.attempt.error_type"));

        ActivityEvent second = capture.Events[1];
        Assert.Equal(2, GetTag(second, "routing.attempt.ordinal"));
        Assert.Equal("fallback", GetTag(second, "routing.attempt.model"));
        Assert.Equal("success", GetTag(second, "routing.attempt.outcome"));
        Assert.Null(GetTag(second, "routing.attempt.error_type"));
    }

    [Fact]
    public async Task AttemptEvents_RecordErrorWhenChainExhausted()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));
        using var client = new RoutingChatClient(
            [new RoutingChatModel("a", client: failing1), new RoutingChatModel("b", client: failing2)],
            selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")]));
        }

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal("fallback", GetTag(capture.Events[0], "routing.attempt.outcome"));
        Assert.Equal("error", GetTag(capture.Events[1], "routing.attempt.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, GetTag(capture.Events[1], "routing.attempt.error_type"));
    }

    [Fact]
    public async Task AttemptEvents_SingleSuccess_RecordsOneEvent()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new RoutingChatClient([new RoutingChatModel("only", client: working)]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        ActivityEvent only = Assert.Single(capture.Events);
        Assert.Equal("only", GetTag(only, "routing.attempt.model"));
        Assert.Equal("success", GetTag(only, "routing.attempt.outcome"));
        Assert.NotNull(GetTag(only, "routing.attempt.duration_ms"));
    }

    [Fact]
    public async Task AttemptEvents_Streaming_RecordsFallbackThenFirstTokenSuccess()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Models));
        using var client = new RoutingChatClient(
            [new RoutingChatModel("primary", client: failing), new RoutingChatModel("fallback", client: working)],
            selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
            {
                // drain
            }
        }

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal("fallback", GetTag(capture.Events[0], "routing.attempt.outcome"));
        Assert.Equal("success", GetTag(capture.Events[1], "routing.attempt.outcome"));
        Assert.Equal("fallback", GetTag(capture.Events[1], "routing.attempt.model"));
    }

    [Fact]
    public async Task AttemptEvents_NotEmitted_WhenNoListenerRecording()
    {
        // No ActivityListener is registered, so Activity.Current is null and the router must skip the events
        // entirely (and not throw) — telemetry cost is only paid when something is collecting it.
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };
        using var client = new RoutingChatClient([new RoutingChatModel("only", client: working)]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
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
        var selector = new ComplexityChatRouteSelector(
            new Dictionary<ChatComplexityTier, string> { [ChatComplexityTier.Simple] = "m1" },
            defaultModel: "m1");

        using var client = new RoutingChatClient([new RoutingChatModel("m1", client: inner)], selector);

        Assert.Same(client, client.GetService(typeof(RoutingChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(selector, client.GetService(typeof(IChatRouteSelector)));
        Assert.Same(selector, client.GetService(typeof(ComplexityChatRouteSelector)));
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
    public async Task CapabilityGate_NarrowsToVisionModel_WhenRequestContainsImage()
    {
        bool plainCalled = false;
        bool visionCalled = false;
        using var plain = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { plainCalled = true; return Task.FromResult(new ChatResponse()); },
        };
        using var vision = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { visionCalled = true; return Task.FromResult(new ChatResponse()); },
        };

        // The default (empty) selector pins to Models[0]; the gate must remove the non-vision first model so
        // the image request routes to the vision-capable one.
        using var client = new RoutingChatClient(
        [
            new RoutingChatModel("plain", client: plain),
            new RoutingChatModel("vision", client: vision, traits: RoutingChatModelTraits.Vision),
        ]);

        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal("vision", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
        Assert.True(visionCalled);
        Assert.False(plainCalled);
    }

    [Fact]
    public async Task CapabilityGate_NarrowsToToolCallingModel_WhenToolsRequested()
    {
        using var plain = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };
        using var tools = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        using var client = new RoutingChatClient(
        [
            new RoutingChatModel("plain", client: plain),
            new RoutingChatModel("tools", client: tools, traits: RoutingChatModelTraits.ToolCalling),
        ]);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "get_status")] };
        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "status?")], options);

        Assert.Equal("tools", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public async Task CapabilityGate_FallsThrough_WhenNoModelDeclaresCapability()
    {
        // Soft gate: when no registered model positively declares the required capability (sparse or wrong
        // trait metadata), the router must not strand the request — it falls back to the full candidate set.
        using var only = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        using var client = new RoutingChatClient([new RoutingChatModel("only", client: only)]);

        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal("only", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
    }

    [Fact]
    public async Task CapabilityGate_Disabled_RoutesToNonCapableModel()
    {
        bool plainCalled = false;
        using var plain = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { plainCalled = true; return Task.FromResult(new ChatResponse()); },
        };
        using var vision = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        // Bypass the gate: the default selector pins to Models[0] even though it lacks Vision.
        using var client = new RoutingChatClientBuilder()
            .AddModel("plain", plain)
            .AddModel("vision", vision, traits: RoutingChatModelTraits.Vision)
            .UseCapabilityGate(false)
            .Build();

        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal("plain", response.AdditionalProperties![RoutingChatClient.SelectedModelNameKey]);
        Assert.True(plainCalled);
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

    private const string ReasoningPrompt = "Let's think step by step and reason through this; analyze this carefully.";

    [Fact]
    public async Task DecisionEvent_RecordsComplexityTier()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };
        var selector = new ComplexityChatRouteSelector(
            new Dictionary<ChatComplexityTier, string> { [ChatComplexityTier.Reasoning] = "m" },
            defaultModel: "m");

        using var client = new RoutingChatClient(
            [new RoutingChatModel("m", modelId: "prov/m", client: inner)],
            selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, ReasoningPrompt)]);
        }

        ActivityEvent decision = Assert.Single(capture.DecisionEvents);
        Assert.Equal("m", GetTag(decision, "routing.selected_model"));
        Assert.Equal("Reasoning", GetTag(decision, "routing.complexity.tier"));
    }

    [Fact]
    public async Task DecisionEvent_RecordsSemanticScore()
    {
        using var aClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };
        using var bClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };

        var vectors = new Dictionary<string, float[]> { ["a-utt"] = [1f, 0f], ["b-utt"] = [0f, 1f], ["q"] = [1f, 0f] };
        using var embedder = VectorEmbeddingGenerator(vectors);
        var profiles = new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["a-utt"], ["b"] = ["b-utt"] };
        var selector = new SemanticChatRouteSelector(embedder, profiles);

        using var client = new RoutingChatClient(
            [new RoutingChatModel("a", client: aClient), new RoutingChatModel("b", client: bClient)],
            selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "q")]);
        }

        ActivityEvent decision = Assert.Single(capture.DecisionEvents);
        Assert.Equal("a", GetTag(decision, "routing.selected_model"));
        Assert.Equal(1.0, Assert.IsType<double>(GetTag(decision, "routing.semantic.score")), 3);
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

    private static object? GetTag(ActivityEvent evt, string key) =>
        evt.Tags.FirstOrDefault(t => t.Key == key).Value;

    // Registers an ActivitySource + listener, starts a recording "turn" activity, and collects the
    // routing.attempt events the router adds to it. Mirrors how a real OpenTelemetry consumer would observe them.
    private sealed class AttemptEventCapture : IDisposable
    {
        private readonly ActivitySource _source = new("RoutingChatClientTests-" + Guid.NewGuid().ToString("N"));
        private readonly ActivityListener _listener;
        private Activity? _turn;

        public AttemptEventCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s == _source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<ActivityEvent> Events =>
            _turn?.Events.Where(e => e.Name == RoutingChatClient.AttemptEventName).ToList() ?? [];

        public IReadOnlyList<ActivityEvent> DecisionEvents =>
            _turn?.Events.Where(e => e.Name == RoutingChatClient.DecisionEventName).ToList() ?? [];

        public IDisposable StartTurn() => _turn = _source.StartActivity("turn")!;

        public void Dispose()
        {
            _turn?.Dispose();
            _listener.Dispose();
            _source.Dispose();
        }
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
