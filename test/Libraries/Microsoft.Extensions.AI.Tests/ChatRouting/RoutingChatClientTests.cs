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
    // The AdditionalProperties key a test route uses to declare capability tokens that a canRoute recipe reads.
    private const string CapabilitiesKey = "capabilities";

    // Builds a route that declares the given capability tokens under CapabilitiesKey.
    private static ChatRoute Capable(string name, IChatClient client, params string[] capabilities) =>
        new(name, additionalProperties: new AdditionalPropertiesDictionary { [CapabilitiesKey] = capabilities }, client: client);

    // Whether a route declares the given capability token under CapabilitiesKey.
    private static bool Declares(ChatRoute route, string token) =>
        route.AdditionalProperties is not null &&
        route.AdditionalProperties.TryGetValue(CapabilitiesKey, out object? value) &&
        value is IEnumerable<string> tokens &&
        tokens.Contains(token, StringComparer.OrdinalIgnoreCase);

    // Whether any message carries image content.
    private static bool HasImage(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Contents.Any(c => c is DataContent d && d.HasTopLevelMediaType("image")));

    [Fact]
    public void Constructor_RejectsEmptyModels()
    {
        Assert.Throws<ArgumentException>(() => new RoutingChatClient([]));
    }

    [Fact]
    public void Constructor_RejectsModelWithoutClient()
    {
        Assert.Throws<ArgumentException>(() => new RoutingChatClient([new ChatRoute("m1")]));
    }

    [Fact]
    public void GetService_ReturnsRoutingChatClientMetadata()
    {
        using var inner = new TestChatClient();
        using var client = new RoutingChatClient([new ChatRoute("m1", client: inner)]);

        // Exposing ChatClientMetadata is what lets UseOpenTelemetry() attribute the router's own span. The
        // provider is the fixed synthetic "routing" (the router fans out to many providers, so no single one
        // is honest here); the model is per-request, so DefaultModelId is null.
        ChatClientMetadata? metadata = client.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("routing", metadata!.ProviderName);
        Assert.Null(metadata.DefaultModelId);
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
            [new ChatRoute("m1", providerName: "openai", modelId: "gpt-4o-mini", client: inner)]);

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
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
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
            new ChatRoute("m1", modelId: "gpt-a", client: c1),
            new ChatRoute("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-b" });

        Assert.Same(r2, response);
        Assert.Equal("m2", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task DefaultSelector_HonorsExplicitModelId_CaseInsensitively()
    {
        // Duplicate-name rejection is case-insensitive, so name matching must be too: a caller whose
        // ChatOptions.ModelId differs from the registered name/id only in case must still resolve to it
        // rather than silently falling through to Routes[0].
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new RoutingChatClient(
        [
            new ChatRoute("first", modelId: "gpt-a", client: c1),
            new ChatRoute("GPT-4o", modelId: "openai/GPT-4o", client: c2),
        ]);

        // Matches the second route by Name (case-insensitive), not the default Routes[0].
        ChatResponse byName = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-4o" });
        Assert.Same(r2, byName);
        Assert.Equal("GPT-4o", byName.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);

        // Matches the second route by ModelId (case-insensitive) as well.
        ChatResponse byId = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "OPENAI/gpt-4o" });
        Assert.Same(r2, byId);
    }

    [Fact]
    public async Task Fallback_WalksPlanOnFailure_AndStampsTheModelThatSucceeded()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));

        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("fallback", client: working)],
            selector);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Fallback_Exhausted_PropagatesLastException()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("a", client: failing1), new ChatRoute("b", client: failing2)],
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
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("other", client: other)],
            selector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal("primary", ex.Message);
        Assert.False(otherCalled);
    }

    [Fact]
    public async Task OnFailure_ReturningRemaining_TriesNonPlanCandidatesInRegistrationOrder()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        // The selector picks a single model; the failure delegate reaches the plan-omitted candidates.
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

        using RoutingChatClient client = new RoutingChatClient(
            [
                new ChatRoute("primary", client: failing),
                new ChatRoute("fallback", client: working),
            ],
            selector: selector,
            onFailure: static ctx => ctx.Remaining);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task OnFailure_ReordersRemaining_ControlsTailOrder()
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

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

        using var client = new RoutingChatClient(
            [
                new ChatRoute("primary", client: failing),
                new ChatRoute("a", client: a),
                new ChatRoute("b", client: b),
            ],
            selector,
            onFailure: ctx =>
            {
                // Reverse the remaining order so "b" is tried before "a".
                var reversed = new List<ChatRoute>(ctx.Remaining);
                reversed.Reverse();
                return reversed;
            });

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("b", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.False(aCalled);
    }

    [Fact]
    public async Task OnFailure_ReturningEmpty_ShortCircuits_AndPropagates()
    {
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("caller-fault") };
        bool nextCalled = false;
        using var next = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                nextCalled = true;
                return Task.FromResult(new ChatResponse());
            },
        };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("next", client: next)],
            selector,
            onFailure: static _ => null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        // Returning null stopped the router at the failing route: the remaining route was never attempted.
        Assert.Equal("caller-fault", ex.Message);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task OnFailure_ReturningRemaining_FallsBackToNextRoute()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("transient") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("fallback", client: working)],
            selector,
            onFailure: static ctx => ctx.Remaining);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("fallback", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task OnFailure_PrunesRoutesSharingFailedProvider()
    {
        // The authentication-error scenario: a 401 for one provider condemns every route on that provider. The
        // delegate looks ahead and prunes the whole provider family in one shot rather than failing each in turn.
        using var openAiA = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("401 unauthorized") };
        bool openAiBCalled = false;
        using var openAiB = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                openAiBCalled = true;
                throw new InvalidOperationException("401 unauthorized");
            },
        };
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var anthropic = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [
                new ChatRoute("openai-a", providerName: "openai", client: openAiA),
                new ChatRoute("openai-b", providerName: "openai", client: openAiB),
                new ChatRoute("anthropic-c", providerName: "anthropic", client: anthropic),
            ],
            selector,
            onFailure: ctx => ctx.Remaining
                .Where(r => !string.Equals(r.ProviderName, ctx.Route.ProviderName, StringComparison.Ordinal))
                .ToList());

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("anthropic-c", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.False(openAiBCalled); // the sibling on the poisoned provider was pruned, never attempted
    }

    [Fact]
    public async Task OnFailure_ReturningAlreadyTriedRoutes_StillTerminates()
    {
        // Even a delegate that keeps handing back every route — including ones already attempted — must terminate:
        // the router drops already-tried routes from the returned set, so the attempt set strictly grows.
        var attempted = new List<string>();
        using var a = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => { attempted.Add("a"); throw new InvalidOperationException("a"); } };
        using var b = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => { attempted.Add("b"); throw new InvalidOperationException("b"); } };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("a", client: a), new ChatRoute("b", client: b)],
            selector,
            onFailure: ctx =>
            {
                // Return the full registry every time; the router must still drop tried routes and terminate.
                var all = new List<ChatRoute>(ctx.Remaining) { ctx.Route };
                return all;
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal(["a", "b"], attempted); // each attempted exactly once
        Assert.Equal("b", ex.Message);
    }

    [Fact]
    public async Task OnFailure_InvokedOnTerminalFailure_WithEmptyRemaining()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        var seen = new List<(int Attempt, bool HasRemaining)>();
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("a", client: failing1), new ChatRoute("b", client: failing2)],
            selector,
            onFailure: ctx =>
            {
                seen.Add((ctx.AttemptNumber, ctx.Remaining.Count > 0));
                return ctx.Remaining; // on the terminal failure Remaining is empty, so the router rethrows
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        // The delegate observed BOTH failures, including the terminal one (empty Remaining), so a stateful
        // delegate can record it — yet the router still rethrew the last exception.
        Assert.Equal(2, seen.Count);
        Assert.Equal((1, true), seen[0]);
        Assert.Equal((2, false), seen[1]);
        Assert.Equal("b", ex.Message);
    }

    [Fact]
    public async Task OnFailure_NullByDefault_FallsBackThroughPlanRoutes()
    {
        // A null delegate preserves the historical behavior: fall back through the plan's routes while they
        // remain, rethrow on the last.
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("fallback", client: working)],
            selector);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
    }

    [Fact]
    public async Task OnFailure_Streaming_ReturningEmpty_ShortCircuitsBeforeFirstUpdate()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        bool nextCalled = false;
        using var next = new TestChatClient
        {
            GetStreamingResponseAsyncCallback = (_, _, _) =>
            {
                nextCalled = true;
                return YieldUpdates("ok");
            },
        };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("next", client: next)],
            selector,
            onFailure: static _ => null);

        int count = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
            {
                count++;
            }
        });

        Assert.Equal(0, count);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task OnFailure_RouterOfRouters_OuterDelegateSeesUnwrappedLeafException()
    {
        // The inner router exhausts and rethrows its leaf's raw exception. When it bubbles to the OUTER router,
        // the outer's failure delegate inspects that real exception and decides to continue to another candidate.
        using var innerLeaf = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("leaf-429") };
        using var inner = new RoutingChatClient([new ChatRoute("leaf", client: innerLeaf)]);

        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var backupClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        Exception? outerSaw = null;
        using var outer = new RoutingChatClient(
            [new ChatRoute("inner", client: inner), new ChatRoute("backup", client: backupClient)],
            selector: ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes)),
            onFailure: ctx =>
            {
                outerSaw ??= ctx.Exception;
                return ctx.Remaining;
            });

        ChatResponse response = await outer.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("backup", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);

        // The outer delegate saw the inner leaf's UNWRAPPED exception, not a routing wrapper.
        Assert.IsType<InvalidOperationException>(outerSaw);
        Assert.Equal("leaf-429", outerSaw!.Message);
    }

    [Fact]
    public async Task AttemptEvents_RecordFallbackThenSuccess()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));
        using var client = new RoutingChatClient(
            [new ChatRoute("primary", modelId: "p", providerName: "prov", client: failing), new ChatRoute("fallback", client: working)],
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
        Assert.Equal("primary", GetTag(first, "routing.attempt.route"));
        Assert.Equal("p", GetTag(first, "routing.attempt.model_id"));
        Assert.Equal("prov", GetTag(first, "routing.attempt.provider"));
        Assert.Equal("fallback", GetTag(first, "routing.attempt.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, GetTag(first, "routing.attempt.error_type"));

        ActivityEvent second = capture.Events[1];
        Assert.Equal(2, GetTag(second, "routing.attempt.ordinal"));
        Assert.Equal("fallback", GetTag(second, "routing.attempt.route"));
        Assert.Equal("success", GetTag(second, "routing.attempt.outcome"));
        Assert.Null(GetTag(second, "routing.attempt.error_type"));
    }

    [Fact]
    public async Task AttemptEvents_RecordErrorWhenChainExhausted()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));
        using var client = new RoutingChatClient(
            [new ChatRoute("a", client: failing1), new ChatRoute("b", client: failing2)],
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

        using var client = new RoutingChatClient([new ChatRoute("only", client: working)]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        ActivityEvent only = Assert.Single(capture.Events);
        Assert.Equal("only", GetTag(only, "routing.attempt.route"));
        Assert.Equal("success", GetTag(only, "routing.attempt.outcome"));
        Assert.NotNull(GetTag(only, "routing.attempt.duration_ms"));
    }

    [Fact]
    public async Task AttemptEvents_Streaming_RecordsFallbackThenFirstTokenSuccess()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));
        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("fallback", client: working)],
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
        Assert.Equal("fallback", GetTag(capture.Events[1], "routing.attempt.route"));
    }

    [Fact]
    public async Task AttemptEvents_NotEmitted_WhenNoListenerRecording()
    {
        // No ActivityListener is registered, so Activity.Current is null and the router must skip the events
        // entirely (and not throw) — telemetry cost is only paid when something is collecting it.
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };
        using var client = new RoutingChatClient([new ChatRoute("only", client: working)]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
    }

    [Fact]
    public async Task SelectorRoutingToUnregisteredModel_Throws()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        var stranger = new ChatRoute("stranger", client: inner);
        IChatRouteSelector selector = ChatRouteSelector.Create(_ => new ChatRoutePlan(stranger));

        using var client = new RoutingChatClient([new ChatRoute("m1", client: inner)], selector);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task Streaming_StampsOnlyTheFirstUpdate()
    {
        using var inner = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("a", "b") };

        using var client = new RoutingChatClient([new ChatRoute("m1", modelId: "x", client: inner)]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("m1", updates[0].AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.True(
            updates[1].AdditionalProperties is null ||
            !updates[1].AdditionalProperties!.ContainsKey(RoutingChatClient.SelectedRouteNameKey));
    }

    [Fact]
    public async Task Streaming_FallsBackBeforeFirstUpdate()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes));

        using var client = new RoutingChatClient(
            [new ChatRoute("primary", client: failing), new ChatRoute("fallback", client: working)],
            selector);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Single(updates);
        Assert.Equal("fallback", updates[0].AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public void GetService_ReturnsClientAndSelector()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };
        var selector = new FixedRouteSelector();

        using var client = new RoutingChatClient([new ChatRoute("m1", client: inner)], selector);

        Assert.Same(client, client.GetService(typeof(RoutingChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(selector, client.GetService(typeof(IChatRouteSelector)));
        Assert.Same(selector, client.GetService(typeof(FixedRouteSelector)));
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
            new ChatRoute("a", client: shared),
            new ChatRoute("b", client: shared),
            new ChatRoute("c", client: other),
        ]);
#pragma warning restore CA2000

        client.Dispose();

        Assert.Equal(1, shared.DisposeCount);
        Assert.Equal(1, other.DisposeCount);
    }

    [Fact]
    public async Task Constructor_BuildsRoutingClient()
    {
        var expected = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(expected) };

        using RoutingChatClient client = new RoutingChatClient(
            [new ChatRoute("m1", modelId: "x", client: inner)]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(expected, response);
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public void Constructor_RejectsDuplicateModelNames()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        Assert.Throws<ArgumentException>(() => new RoutingChatClient(
            [
                new ChatRoute("dup", client: inner),
                new ChatRoute("dup", client: inner),
            ]));
    }

    [Fact]
    public async Task CanRoute_NarrowsToVisionModel_WhenRequestContainsImage()
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

        // A capability recipe expressed as canRoute: when the request carries an image, keep only vision-capable
        // routes. The default (empty) selector then pins to the sole survivor.
        using var client = new RoutingChatClient(
        [
            new ChatRoute("plain", client: plain),
            Capable("vision", vision, "vision"),
        ],
        canRoute: (route, messages, _) => !HasImage(messages) || Declares(route, "vision"));

        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal("vision", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.True(visionCalled);
        Assert.False(plainCalled);
    }

    [Fact]
    public async Task CanRoute_NarrowsToToolCallingModel_WhenToolsRequested()
    {
        using var plain = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };
        using var tools = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        using var client = new RoutingChatClient(
        [
            new ChatRoute("plain", client: plain),
            Capable("tools", tools, "function_calling"),
        ],
        canRoute: (route, _, options) => options?.Tools is not { Count: > 0 } || Declares(route, "function_calling"));

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "get_status")] };
        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "status?")], options);

        Assert.Equal("tools", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task CanRoute_FallsThrough_WhenPredicateAdmitsNoRoute()
    {
        // Soft filter: when the predicate excludes every route (for example every route is momentarily
        // unavailable), the router must not strand the request — it falls back to the full candidate set.
        using var only = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        using var client = new RoutingChatClient(
            [new ChatRoute("only", client: only)],
            canRoute: static (_, _, _) => false);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal("only", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task CanRoute_Null_AppliesNoFilter()
    {
        bool plainCalled = false;
        using var plain = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { plainCalled = true; return Task.FromResult(new ChatResponse()); },
        };
        using var vision = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        // With no candidate filter, every route is a candidate: the default selector pins Routes[0] even for an
        // image request.
        using var client = new RoutingChatClient(
        [
            new ChatRoute("plain", client: plain),
            Capable("vision", vision, "vision"),
        ]);

        var message = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([message]);

        Assert.Equal("plain", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.True(plainCalled);
    }

    [Fact]
    public async Task StickySelectorClosure_ShortCircuitsToPinnedModel_ThenDefersWhenAbsent()
    {
        // Conversation stickiness is an APP policy, not a router mechanism: the app stamps a pinned model name onto
        // ChatOptions per turn and supplies a selector that honors it by resolving that name against ctx.Routes,
        // falling back to its base policy when the pin is absent or names no candidate. This proves the selector
        // seam alone is sufficient to express stickiness with a tiny closure and no library-side pin machinery.
        const string StickyKey = "app.sticky";

        var a = new ChatResponse(new ChatMessage(ChatRole.Assistant, "a"));
        var b = new ChatResponse(new ChatMessage(ChatRole.Assistant, "b"));
        using var ca = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(a) };
        using var cb = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(b) };

        // Base policy always picks the first model ("a"); the sticky pin must override it when present.
        IChatRouteSelector sticky = ChatRouteSelector.Create(ctx =>
        {
            string? pin = ctx.Options?.AdditionalProperties is { } props && props.TryGetValue(StickyKey, out object? v)
                ? v as string
                : null;

            // Resolve the pin against the live candidates (reference identity preserved); never reconstruct a route.
            ChatRoute? hit = pin is null
                ? null
                : ctx.Routes.FirstOrDefault(r => string.Equals(r.Name, pin, StringComparison.OrdinalIgnoreCase));
            return hit is not null ? new ChatRoutePlan(hit) : new ChatRoutePlan(ctx.Routes[0]);
        });

        using var client = new RoutingChatClient(
            [new ChatRoute("a", client: ca), new ChatRoute("b", client: cb)],
            sticky);

        // Pin "b": the closure short-circuits selection to it.
        var pinned = new ChatOptions { AdditionalProperties = new() { [StickyKey] = "b" } };
        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], pinned);
        Assert.Same(b, response);
        Assert.Equal("b", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);

        // No pin: the closure defers to the base policy ("a").
        ChatResponse plain = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Same(a, plain);
        Assert.Equal("a", plain.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task DecisionEvent_RecordsSelectorDecisionMetadata()
    {
        // The router copies every entry a selector attaches to ChatRoutePlan.DecisionMetadata verbatim onto the
        // routing.decision event, alongside the model it selected. That projection is the core mechanism the
        // concrete selectors (complexity tier, semantic score) rely on; it is asserted here with a fake selector
        // so the core does not depend on any selector from Microsoft.Extensions.AI.Routing.
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };
        var selector = new FixedRouteSelector(new Dictionary<string, object>
        {
            ["routing.custom.tier"] = "Reasoning",
            ["routing.custom.score"] = 0.875,
        });

        using var client = new RoutingChatClient([new ChatRoute("m", modelId: "prov/m", client: inner)], selector);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        ActivityEvent decision = Assert.Single(capture.DecisionEvents);
        Assert.Equal("m", GetTag(decision, "routing.selected_route"));
        Assert.Equal("Reasoning", GetTag(decision, "routing.custom.tier"));
        Assert.Equal(0.875, Assert.IsType<double>(GetTag(decision, "routing.custom.score")));
    }

    [Fact]
    public async Task Nesting_LeafWinsIdentity_AndPathAccumulates()
    {
        // A router-of-routers: the outer router's single "model" is itself an inner RoutingChatClient whose leaf
        // is the concrete provider model. The response must identify the LEAF that produced the tokens — not the
        // outer "Complexity" wrapper, whose ModelId/ProviderName are null — while the path records the full route.
        var leafResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var leafClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(leafResponse) };

        using var inner = new RoutingChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new RoutingChatClient(
            [new ChatRoute("Complexity", client: inner)]);

        ChatResponse response = await outer.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(leafResponse, response);
        AdditionalPropertiesDictionary props = response.AdditionalProperties!;
        Assert.Equal("gpt-4o-mini", props[RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal("openai/gpt-4o-mini", props[RoutingChatClient.SelectedModelIdKey]);
        Assert.Equal("openai", props[RoutingChatClient.SelectedProviderNameKey]);
        Assert.Equal("Complexity/gpt-4o-mini", props[RoutingChatClient.SelectedPathKey]);
    }

    [Fact]
    public async Task Nesting_Streaming_LeafWinsIdentity_AndPathAccumulates()
    {
        // Same composition, streaming: both routers stamp the same first update object as it flows up. The inner
        // (leaf) stamp must win for identity, and the outer must prepend its segment to the path.
        using var leafClient = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };
        using var inner = new RoutingChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new RoutingChatClient(
            [new ChatRoute("Complexity", client: inner)]);

        ChatResponseUpdate? first = null;
        await foreach (ChatResponseUpdate update in outer.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            first ??= update;
        }

        Assert.NotNull(first);
        AdditionalPropertiesDictionary props = first!.AdditionalProperties!;
        Assert.Equal("gpt-4o-mini", props[RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal("openai/gpt-4o-mini", props[RoutingChatClient.SelectedModelIdKey]);
        Assert.Equal("Complexity/gpt-4o-mini", props[RoutingChatClient.SelectedPathKey]);
    }

    [Fact]
    public async Task Nesting_WhenRoutingSourceSubscribed_ProducesSpanTreeWithPerRouterEvents()
    {
        // When a listener subscribes to the routing ActivitySource, each router opens its OWN span, so nested
        // routers form a tree and each router's decision/attempt events are scoped to its span. This is the fix
        // for events colliding on a single shared activity: both attempts legitimately carry ordinal 1 because
        // they live on different spans.
        var leafResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var leafClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(leafResponse) };
        using var inner = new RoutingChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new RoutingChatClient(
            [new ChatRoute("Complexity", client: inner)]);

        using var capture = new RoutingSpanCapture();
        _ = await outer.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(2, capture.Spans.Count);

        Activity innerSpan = Assert.Single(capture.Spans, s => SpanSelectedModel(s) == "gpt-4o-mini");
        Activity outerSpan = Assert.Single(capture.Spans, s => SpanSelectedModel(s) == "Complexity");

        Assert.Equal(outerSpan.SpanId, innerSpan.ParentSpanId);
        Assert.Equal(1, SpanAttemptOrdinal(innerSpan));
        Assert.Equal(1, SpanAttemptOrdinal(outerSpan));
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

    // Subscribes to the routing ActivitySource so every RoutingChatClient opens its own span, and collects those
    // spans when they stop (their events are final at that point). Lets a test observe routing as a span tree and
    // assert each router's events are scoped to its own span rather than sharing one ambient activity.
    private sealed class RoutingSpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        public RoutingSpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == RoutingChatClient.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Spans.Add,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Spans { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private static string? SpanSelectedModel(Activity span) =>
        (string?)GetTag(Assert.Single(span.Events, e => e.Name == RoutingChatClient.DecisionEventName), "routing.selected_route");

    private static int SpanAttemptOrdinal(Activity span) =>
        (int)GetTag(Assert.Single(span.Events, e => e.Name == RoutingChatClient.AttemptEventName), "routing.attempt.ordinal")!;

    // A minimal IChatRouteSelector for exercising the router's selector plumbing (GetService exposure and the
    // decision-metadata projection) without depending on a concrete selector from Microsoft.Extensions.AI.Routing.
    // It routes to the context's routes in their given order and surfaces the supplied decision metadata on the plan.
    private sealed class FixedRouteSelector : IChatRouteSelector
    {
        private readonly IReadOnlyDictionary<string, object>? _decisionMetadata;

        public FixedRouteSelector(IReadOnlyDictionary<string, object>? decisionMetadata = null)
        {
            _decisionMetadata = decisionMetadata;
        }

        public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default) =>
            new(new ChatRoutePlan(context.Routes, _decisionMetadata));
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
