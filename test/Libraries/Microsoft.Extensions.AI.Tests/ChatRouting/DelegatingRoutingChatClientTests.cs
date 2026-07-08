// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class DelegatingRoutingChatClientTests
{
    private static readonly List<ChatMessage> _messages = [new(ChatRole.User, "hello")];

    private static IChatRouteSelector PickAll() =>
        ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

    [Fact]
    public void Constructor_RejectsEmptyRoutes()
    {
        using var inner = new TestChatClient();
        Assert.Throws<ArgumentException>(() => new DelegatingRoutingChatClient(inner, []));
    }

    [Fact]
    public void Constructor_AllowsMetadataOnlyRoutes()
    {
        // Unlike RoutingChatClient, the middleware dispatches to its single inner client, so routes need no
        // bound client of their own — a metadata-only route is valid here.
        using var inner = new TestChatClient();
        using var client = new DelegatingRoutingChatClient(inner, [new ChatRoute("m1")]);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_RejectsDuplicateRouteNames()
    {
        using var inner = new TestChatClient();
        Assert.Throws<ArgumentException>(() =>
            new DelegatingRoutingChatClient(inner, [new ChatRoute("dup"), new ChatRoute("dup")]));
    }

    [Fact]
    public async Task DefaultSelector_DispatchesToInner_ShapesModelId_AndStampsResponse()
    {
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        ChatOptions? forwarded = null;

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (m, o, _) =>
            {
                Assert.Same(_messages, m);
                forwarded = o;
                return Task.FromResult(expected);
            }
        };

        using var client = new DelegatingRoutingChatClient(
            inner, [new ChatRoute("m1", providerName: "openai", modelId: "gpt-4o-mini")]);

        ChatResponse response = await client.GetResponseAsync(_messages, new ChatOptions(), CancellationToken.None);

        Assert.Same(expected, response);

        // The route's model id was applied to the request forwarded to the single inner client.
        Assert.NotNull(forwarded);
        Assert.Equal("gpt-4o-mini", forwarded!.ModelId);

        // And the chosen route is stamped onto the response, exactly as RoutingChatClient does.
        Assert.NotNull(response.AdditionalProperties);
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal("gpt-4o-mini", response.AdditionalProperties[RoutingChatClient.SelectedModelIdKey]);
    }

    [Fact]
    public async Task ShapesReasoningEffort_WhenCallerDidNotPinIt()
    {
        ChatOptions? forwarded = null;
        var options = new ChatOptions();

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
            {
                forwarded = o;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            }
        };

        using var client = new DelegatingRoutingChatClient(
            inner, [new ChatRoute("m1", reasoningEffort: ReasoningEffort.High)], PickAll());

        _ = await client.GetResponseAsync(_messages, options, CancellationToken.None);

        // The effort is applied on a clone, never mutating the caller's options.
        Assert.NotNull(forwarded);
        Assert.NotSame(options, forwarded);
        Assert.Equal(ReasoningEffort.High, forwarded!.Reasoning!.Effort);
        Assert.Null(options.Reasoning);
    }

    [Fact]
    public async Task CallerPinnedModelIdAndEffort_AreNotClobbered()
    {
        ChatOptions? forwarded = null;
        var options = new ChatOptions
        {
            ModelId = "pinned-model",
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        };

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
            {
                forwarded = o;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            }
        };

        using var client = new DelegatingRoutingChatClient(
            inner, [new ChatRoute("m1", modelId: "route-model", reasoningEffort: ReasoningEffort.High)], PickAll());

        _ = await client.GetResponseAsync(_messages, options, CancellationToken.None);

        // An explicit request always wins over a route default; since nothing needed shaping, the caller's
        // options are forwarded as-is.
        Assert.Same(options, forwarded);
        Assert.Equal("pinned-model", forwarded!.ModelId);
        Assert.Equal(ReasoningEffort.Low, forwarded!.Reasoning!.Effort);
    }

    [Fact]
    public async Task UseRouting_ComposesInBuilderPipeline()
    {
        ChatOptions? forwarded = null;
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
            {
                forwarded = o;
                return Task.FromResult(expected);
            }
        };

        using IChatClient pipeline = inner.AsBuilder()
            .UseRouting([new ChatRoute("m1", modelId: "gpt-4o-mini")])
            .Build();

        ChatResponse response = await pipeline.GetResponseAsync(_messages);

        Assert.Same(expected, response);
        Assert.Equal("gpt-4o-mini", forwarded!.ModelId);
    }

    [Fact]
    public void GetService_PassesThroughToInnerClient_PreservingIdentity()
    {
        // The middleware wraps exactly one client, so it reports that client's identity rather than a synthetic
        // "routing" provider: downstream telemetry sees the real provider.
        using var inner = new TestChatClient();
        var innerMetadata = new ChatClientMetadata("openai", defaultModelId: "gpt-4o");
        inner.GetServiceCallback = (serviceType, serviceKey) =>
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return innerMetadata;
            }

            return serviceKey is null && serviceType.IsInstanceOfType(inner) ? inner : null;
        };

        using var client = new DelegatingRoutingChatClient(inner, [new ChatRoute("m1", modelId: "x")]);

        ChatClientMetadata? metadata = client.GetService<ChatClientMetadata>();

        Assert.Same(innerMetadata, metadata);
        Assert.Equal("openai", metadata!.ProviderName);
    }

    [Fact]
    public void GetService_ExposesSelector()
    {
        using var inner = new TestChatClient();
        IChatRouteSelector selector = PickAll();

        using var client = new DelegatingRoutingChatClient(inner, [new ChatRoute("m1")], selector);

        Assert.Same(selector, client.GetService<IChatRouteSelector>());
        Assert.Same(client, client.GetService<DelegatingRoutingChatClient>());
    }

    [Fact]
    public async Task Streaming_DispatchesToInner_AndShapesModelId()
    {
        ChatOptions? forwarded = null;

        using var inner = new TestChatClient
        {
            GetStreamingResponseAsyncCallback = (_, o, _) =>
            {
                forwarded = o;
                return YieldUpdates("a", "b");
            }
        };

        using var client = new DelegatingRoutingChatClient(inner, [new ChatRoute("m1", modelId: "gpt-4o-mini")]);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(_messages))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("gpt-4o-mini", forwarded!.ModelId);

        // The chosen route is stamped onto the first streamed update.
        Assert.Equal("m1", updates[0].AdditionalProperties?[RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Fallback_WalksToNextRoute_WhenFirstFails()
    {
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
        bool secondCalled = false;

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
            {
                // First attempt (primary route) throws; second attempt (fallback route) succeeds. The middleware
                // reuses the same shared engine, so fallback semantics match RoutingChatClient.
                if (o?.ModelId == "primary-model")
                {
                    throw new InvalidOperationException("boom");
                }

                secondCalled = true;
                return Task.FromResult(expected);
            }
        };

        using var client = new DelegatingRoutingChatClient(
            inner,
            [new ChatRoute("primary", modelId: "primary-model"), new ChatRoute("fallback", modelId: "fallback-model")],
            PickAll());

        ChatResponse response = await client.GetResponseAsync(_messages, new ChatOptions(), CancellationToken.None);

        Assert.True(secondCalled);
        Assert.Same(expected, response);
        Assert.Equal("fallback", response.AdditionalProperties?[RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public void GetService_NullServiceType_Throws()
    {
        using var inner = new TestChatClient();
        using var client = new DelegatingRoutingChatClient(inner, [new ChatRoute("m1")]);

        Assert.Throws<ArgumentNullException>(() => client.GetService(null!));
    }

    [Fact]
    public async Task CanRoute_NarrowsCandidates_ForSingleClient()
    {
        // The candidate filter behaves identically on the single-client front door: only admitted routes reach the
        // selector, and the surviving route's model id is shaped onto the request forwarded to the inner client.
        ChatOptions? forwarded = null;
        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
            {
                forwarded = o;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            }
        };

        using var client = new DelegatingRoutingChatClient(
            inner,
            [new ChatRoute("cheap", modelId: "cheap-model"), new ChatRoute("premium", modelId: "premium-model")],
            canRoute: (route, _, _) => route.Name == "premium");

        ChatResponse response = await client.GetResponseAsync(_messages);

        Assert.Equal("premium", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal("premium-model", forwarded!.ModelId);
    }

    [Fact]
    public async Task OnFailure_FallsBackToPlanOmittedRoute()
    {
        // The default selector picks a single route; supplying onFailure lets the middleware reach a plan-omitted
        // candidate on failure, exactly as RoutingChatClient does, because both drive the same engine.
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) =>
                o?.ModelId == "primary-model"
                    ? throw new InvalidOperationException("boom")
                    : Task.FromResult(good),
        };

        using var client = new DelegatingRoutingChatClient(
            inner,
            [new ChatRoute("primary", modelId: "primary-model"), new ChatRoute("fallback", modelId: "fallback-model")],
            onFailure: ctx => ctx.Remaining);

        ChatResponse response = await client.GetResponseAsync(_messages);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Streaming_FallsBackBeforeFirstUpdate()
    {
        using var inner = new TestChatClient
        {
            GetStreamingResponseAsyncCallback = (_, o, _) =>
                o?.ModelId == "primary-model" ? ThrowingStream() : YieldUpdates("ok"),
        };

        using var client = new DelegatingRoutingChatClient(
            inner,
            [new ChatRoute("primary", modelId: "primary-model"), new ChatRoute("fallback", modelId: "fallback-model")],
            PickAll());

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(_messages))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("fallback", updates[0].AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
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

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldUpdates(params string[] texts)
    {
        foreach (string text in texts)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }
    }
}
