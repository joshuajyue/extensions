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
    // The AdditionalProperties key a test route uses to declare capability tokens that a policy reads.
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

    // The first registered route not yet attempted, in registration order, or null when exhausted.
    private static ChatRoute? FirstUnattempted(IReadOnlyList<ChatRoute> routes, IReadOnlyList<ChatRoute> attempted) =>
        routes.FirstOrDefault(r => !attempted.Contains(r));

    [Fact]
    public void Constructor_RejectsEmptyRoutes()
    {
        Assert.Throws<ArgumentException>(() => new FailoverChatClient([]));
    }

    [Fact]
    public void Constructor_RejectsRouteWithoutClient()
    {
        Assert.Throws<ArgumentException>(() => new FailoverChatClient([new ChatRoute("m1")]));
    }

    [Fact]
    public void Constructor_RejectsDuplicateNamesCaseInsensitively()
    {
        using var inner = new TestChatClient();
        Assert.Throws<ArgumentException>(() => new FailoverChatClient(
        [
            new ChatRoute("m1", client: inner),
            new ChatRoute("M1", client: inner),
        ]));
    }

    [Fact]
    public void GetService_ReturnsRoutingChatClientMetadata()
    {
        using var inner = new TestChatClient();
        using var client = new FailoverChatClient([new ChatRoute("m1", client: inner)]);

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        Assert.NotNull(metadata);
        Assert.Equal("routing", metadata!.ProviderName);
    }

    [Fact]
    public void GetService_ReturnsSelf_AndNullForUnknownOrKeyed()
    {
        using var inner = new TestChatClient();
        using var client = new FailoverChatClient([new ChatRoute("m1", client: inner)]);

        // The concrete client and its abstract base both resolve to the instance.
        Assert.Same(client, client.GetService(typeof(FailoverChatClient)));
        Assert.Same(client, client.GetService(typeof(RoutingChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));

        // A keyed lookup or an unrelated type resolves to nothing.
        Assert.Null(client.GetService(typeof(FailoverChatClient), serviceKey: "k"));
        Assert.Null(client.GetService(typeof(string)));
    }

    [Fact]
    public async Task Failover_ForwardsModelId_StampsResponse_AndDoesNotLeakIntoRequest()
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

        using var client = new FailoverChatClient(
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
        Assert.Equal("m1", response.AdditionalProperties[RoutingChatClient.SelectedPathKey]);
    }

    [Fact]
    public async Task Failover_HonorsExplicitModelId()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("m1", modelId: "gpt-a", client: c1),
            new ChatRoute("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-b" });

        Assert.Same(r2, response);
        Assert.Equal("m2", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Failover_HonorsExplicitModelId_CaseInsensitively()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("first", modelId: "gpt-a", client: c1),
            new ChatRoute("GPT-4o", modelId: "openai/GPT-4o", client: c2),
        ]);

        // Matches the second route by Name (case-insensitive), not the default first route.
        ChatResponse byName = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-4o" });
        Assert.Same(r2, byName);
        Assert.Equal("GPT-4o", byName.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);

        // Matches the second route by ModelId (case-insensitive) as well.
        ChatResponse byId = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "OPENAI/gpt-4o" });
        Assert.Same(r2, byId);
    }

    [Fact]
    public async Task Failover_ExplicitModelId_NoMatch_FallsBackToFirstRoute()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("m1", modelId: "gpt-a", client: c1),
            new ChatRoute("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "does-not-exist" });

        Assert.Same(r1, response);
        Assert.Equal("m1", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Failover_WalksRoutesOnFailure_AndStampsTheRouteThatSucceeded()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("primary", client: failing),
            new ChatRoute("backup", client: working),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
        Assert.Equal("backup", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Failover_Exhausted_PropagatesLastException()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("m1", client: failing1),
            new ChatRoute("m2", client: failing2),
        ]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
        Assert.Equal("b", ex.Message);
    }

    [Fact]
    public async Task Failover_Streaming_FallsBackBeforeFirstUpdate()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("primary", client: failing),
            new ChatRoute("backup", client: working),
        ]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        ChatResponseUpdate only = Assert.Single(updates);
        Assert.Equal("ok", only.Text);
        Assert.Equal("backup", only.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Failover_ForwardsReasoningEffort_WhenCallerPinsNeither()
    {
        ChatOptions? forwarded = null;
        var options = new ChatOptions();
        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) => { forwarded = o; return Task.FromResult(new ChatResponse()); },
        };

        using var client = new FailoverChatClient(
            [new ChatRoute("m1", modelId: "gpt-x", reasoningEffort: ReasoningEffort.High, client: inner)]);

        _ = await client.GetResponseAsync([new(ChatRole.User, "hi")], options);

        Assert.NotNull(forwarded);
        Assert.NotSame(options, forwarded);
        Assert.Equal("gpt-x", forwarded!.ModelId);
        Assert.Equal(ReasoningEffort.High, forwarded.Reasoning!.Effort);
        Assert.Null(options.Reasoning);
    }

    [Fact]
    public async Task Streaming_StampsOnlyTheFirstUpdate()
    {
        using var inner = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("a", "b") };

        using var client = new FailoverChatClient([new ChatRoute("m1", modelId: "x", client: inner)]);

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
    public async Task Policy_SelectsRouteByCustomLogic()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));
        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        // A policy that always picks the second route on the first selection.
        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: c1), new ChatRoute("m2", client: c2)],
            (_, _, routes, attempted, _) => attempted.Count == 0 ? routes[1] : null);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(r2, response);
        Assert.Equal("m2", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Policy_CandidateFiltering_SelectsVisionRouteForImageRequest()
    {
        var plainResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "plain"));
        var visionResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "vision"));
        using var plain = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(plainResponse) };
        using var vision = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(visionResponse) };

        // The policy narrows to vision-capable routes only when the request carries an image, then picks the
        // first eligible route not yet attempted — the new single-seam equivalent of the old canRoute filter.
        using var client = new DelegatingTestRouter(
            [Capable("plain", plain), Capable("vision", vision, "vision")],
            (messages, _, routes, attempted, _) =>
            {
                IEnumerable<ChatRoute> eligible = HasImage(messages) ? routes.Where(r => Declares(r, "vision")) : routes;
                return eligible.FirstOrDefault(r => !attempted.Contains(r));
            });

        var imageMessage = new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
        ChatResponse response = await client.GetResponseAsync([imageMessage]);

        Assert.Same(visionResponse, response);
        Assert.Equal("vision", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Policy_FailureHandling_PrunesRoutesSharingFailedProvider()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var openAiA = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("401 unauthorized") };
        using var openAiB = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "should-not-run"))) };
        using var anthropic = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new DelegatingTestRouter(
        [
            new ChatRoute("openai-a", providerName: "openai", client: openAiA),
            new ChatRoute("openai-b", providerName: "openai", client: openAiB),
            new ChatRoute("anthropic", providerName: "anthropic", client: anthropic),
        ],
        (_, _, routes, attempted, lastException) =>
        {
            if (attempted.Count == 0)
            {
                return routes[0];
            }

            // On an auth error, drop every route that shares the failed route's provider.
            ChatRoute failed = attempted[attempted.Count - 1];
            bool authError = lastException!.Message.Contains("401");
            return routes.FirstOrDefault(r =>
                !attempted.Contains(r) &&
                !(authError && r.ProviderName == failed.ProviderName));
        });

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        // openai-b was pruned because it shares openai-a's provider on the auth error; anthropic answered.
        Assert.Same(good, response);
        Assert.Equal("anthropic", response.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
    }

    [Fact]
    public async Task Policy_FailureHandling_ObservesEveryFailure_IncludingTheLast()
    {
        var seenExceptions = new List<string>();
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        using var client = new DelegatingTestRouter(
        [
            new ChatRoute("m1", client: failing1),
            new ChatRoute("m2", client: failing2),
        ],
        (_, _, routes, attempted, lastException) =>
        {
            if (lastException is not null)
            {
                seenExceptions.Add(lastException.Message);
            }

            return FirstUnattempted(routes, attempted);
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal("b", ex.Message);

        // The policy observed both failures — the terminal one included — even though routing then rethrew.
        Assert.Equal(["a", "b"], seenExceptions);
    }

    [Fact]
    public async Task Policy_ReturningUnregisteredRoute_Throws()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };
        var stranger = new ChatRoute("stranger", client: inner);

        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: inner)],
            (_, _, _, _, _) => stranger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task Policy_ReturningAlreadyAttemptedRoute_TerminatesAndRethrows()
    {
        var attempts = new List<string>();
        using var failing = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { attempts.Add("m1"); throw new InvalidOperationException("boom"); },
        };

        // A buggy policy that keeps returning the same (already-attempted) route must not loop forever: the base
        // collapses an already-attempted route to termination, so the last exception is rethrown after one attempt.
        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: failing)],
            (_, _, routes, _, _) => routes[0]);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));

        Assert.Equal("boom", ex.Message);
        Assert.Equal(["m1"], attempts);
    }

    [Fact]
    public async Task Policy_ReturningNull_OnFirstCall_Throws()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse()) };

        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: inner)],
            (_, _, _, _, _) => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task Policy_ReturningNull_AfterFailure_Rethrows()
    {
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("caller-fault") };
        using var next = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused"))) };

        // The policy picks the first route, then stops (returns null) instead of falling back to the available second.
        using var client = new DelegatingTestRouter(
        [
            new ChatRoute("m1", client: failing),
            new ChatRoute("m2", client: next),
        ],
        (_, _, routes, attempted, _) => attempted.Count == 0 ? routes[0] : null);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")]));
        Assert.Equal("caller-fault", ex.Message);
    }

    [Fact]
    public async Task Cancellation_DuringDispatch_IsNotTreatedAsFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool secondCalled = false;
        using var canceling = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, ct) => throw new OperationCanceledException(ct),
        };
        using var backup = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) => { secondCalled = true; return Task.FromResult(new ChatResponse()); },
        };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("primary", client: canceling),
            new ChatRoute("backup", client: backup),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: cts.Token));

        // A cancellation never triggers fallback.
        Assert.False(secondCalled);
    }

    [Fact]
    public async Task AttemptEvents_RecordFallbackThenSuccess()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("primary", modelId: "p", providerName: "prov", client: failing),
            new ChatRoute("backup", modelId: "b", client: working),
        ]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal("fallback", GetTag(capture.Events[0], "routing.attempt.outcome"));
        Assert.Equal("primary", GetTag(capture.Events[0], "routing.attempt.route"));
        Assert.Equal(1, GetTag(capture.Events[0], "routing.attempt.ordinal"));
        Assert.Equal(typeof(InvalidOperationException).FullName, GetTag(capture.Events[0], "routing.attempt.error_type"));
        Assert.Equal("success", GetTag(capture.Events[1], "routing.attempt.outcome"));
        Assert.Equal("backup", GetTag(capture.Events[1], "routing.attempt.route"));
        Assert.Equal(2, GetTag(capture.Events[1], "routing.attempt.ordinal"));
    }

    [Fact]
    public async Task AttemptEvents_RecordErrorWhenChainExhausted()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("m1", client: failing1),
            new ChatRoute("m2", client: failing2),
        ]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")]));
        }

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal("fallback", GetTag(capture.Events[0], "routing.attempt.outcome"));
        Assert.Equal("error", GetTag(capture.Events[1], "routing.attempt.outcome"));
    }

    [Fact]
    public async Task AttemptEvents_SingleSuccess_RecordsOneEvent()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new FailoverChatClient([new ChatRoute("only", client: working)]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        ActivityEvent evt = Assert.Single(capture.Events);
        Assert.Equal("success", GetTag(evt, "routing.attempt.outcome"));
        Assert.Equal(1, GetTag(evt, "routing.attempt.ordinal"));
    }

    [Fact]
    public async Task AttemptEvents_Streaming_RecordsFallbackThenFirstTokenSuccess()
    {
        using var failing = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => ThrowingStream() };
        using var working = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };

        using var client = new FailoverChatClient(
        [
            new ChatRoute("primary", client: failing),
            new ChatRoute("backup", client: working),
        ]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
            {
                updates.Add(update);
            }

            Assert.NotEmpty(updates);
        }

        Assert.Equal(2, capture.Events.Count);
        Assert.Equal("fallback", GetTag(capture.Events[0], "routing.attempt.outcome"));
        Assert.Equal("success", GetTag(capture.Events[1], "routing.attempt.outcome"));
    }

    [Fact]
    public async Task AttemptEvents_NotEmitted_WhenNoListenerRecording()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new FailoverChatClient([new ChatRoute("only", client: working)]);

        // No ActivityListener subscribed: the router does no telemetry work and the call still succeeds.
        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Same(good, response);
    }

    [Fact]
    public async Task DecisionEvent_RecordsSelectedRoute()
    {
        using var inner = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))) };

        using var client = new FailoverChatClient([new ChatRoute("m", modelId: "prov/m", client: inner)]);

        using var capture = new AttemptEventCapture();
        using (capture.StartTurn())
        {
            _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);
        }

        ActivityEvent decision = Assert.Single(capture.DecisionEvents);
        Assert.Equal("m", GetTag(decision, "routing.selected_route"));
    }

    [Fact]
    public void Dispose_DisposesEachClientExactlyOnce()
    {
#pragma warning disable CA2000 // Dispose objects before losing scope
        var shared = new CountingDisposeClient();
        var other = new CountingDisposeClient();

        var client = new FailoverChatClient(
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
    public async Task Messages_NonListEnumerable_IsMaterializedOnce_AndSharedByEveryStage()
    {
        // A lazily-generated message sequence must be enumerated exactly once and the same snapshot handed to both
        // the policy and the dispatched client, so a generator with side effects is never re-run per stage.
        int enumerations = 0;
        IEnumerable<ChatMessage> Lazy()
        {
            enumerations++;
            yield return new ChatMessage(ChatRole.User, "hi");
        }

        IEnumerable<ChatMessage>? policySaw = null;
        IEnumerable<ChatMessage>? dispatchSaw = null;

        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (m, _, _) => { dispatchSaw = m; return Task.FromResult(new ChatResponse()); },
        };

        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: inner)],
            (messages, _, routes, _, _) => { policySaw = messages; return routes[0]; });

        _ = await client.GetResponseAsync(Lazy());

        Assert.Equal(1, enumerations);
        Assert.Same(policySaw, dispatchSaw);
    }

    [Fact]
    public async Task Nesting_LeafWinsIdentity_AndPathAccumulates()
    {
        // A router-of-routers: the outer router's single route's client is itself a RoutingChatClient whose leaf is
        // the concrete provider model. The response must identify the LEAF that produced the tokens — not the outer
        // "Complexity" wrapper, whose ModelId/ProviderName are null — while the path records the full route.
        var leafResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var leafClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(leafResponse) };

        using var inner = new FailoverChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new FailoverChatClient(
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
        using var leafClient = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };
        using var inner = new FailoverChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new FailoverChatClient(
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
        var leafResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        using var leafClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(leafResponse) };
        using var inner = new FailoverChatClient(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new FailoverChatClient(
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

    // A RoutingChatClient whose selection policy is supplied as a delegate, so a test can express any policy —
    // initial selection, candidate filtering, and failure handling — inline without a bespoke subclass per case.
    private sealed class DelegatingTestRouter : RoutingChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatRoute>, IReadOnlyList<ChatRoute>, Exception?, ChatRoute?> _select;

        public DelegatingTestRouter(
            IReadOnlyList<ChatRoute> routes,
            Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<ChatRoute>, IReadOnlyList<ChatRoute>, Exception?, ChatRoute?> select)
            : base(routes)
        {
            _select = select;
        }

        protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken) =>
            new(_select(messages, options, routes, attempted, lastException));
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
