// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
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
        Assert.Throws<ArgumentException>(() => new OrderedFailoverRouter([]));
    }

    [Fact]
    public void Constructor_RejectsRouteWithoutClient()
    {
        Assert.Throws<ArgumentException>(() => new OrderedFailoverRouter([new ChatRoute("m1")]));
    }

    [Fact]
    public void Constructor_RejectsDuplicateNamesCaseInsensitively()
    {
        using var inner = new TestChatClient();
        Assert.Throws<ArgumentException>(() => new OrderedFailoverRouter(
        [
            new ChatRoute("m1", client: inner),
            new ChatRoute("M1", client: inner),
        ]));
    }

    [Fact]
    public void GetService_ReturnsSelf_AndNullForUnknownOrKeyed()
    {
        using var inner = new TestChatClient();
        using var client = new OrderedFailoverRouter([new ChatRoute("m1", client: inner)]);

        // The concrete client and its abstract base both resolve to the instance.
        Assert.Same(client, client.GetService(typeof(OrderedFailoverRouter)));
        Assert.Same(client, client.GetService(typeof(RoutingChatClient)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));

        // A keyed lookup or an unrelated type resolves to nothing.
        Assert.Null(client.GetService(typeof(OrderedFailoverRouter), serviceKey: "k"));
        Assert.Null(client.GetService(typeof(ChatClientMetadata)));
        Assert.Null(client.GetService(typeof(string)));
    }

    [Fact]
    public async Task Dispatch_PassesMessagesOptionsAndResponseThroughUnchanged()
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

        using var client = new OrderedFailoverRouter(
            [new ChatRoute("m1", providerName: "openai", modelId: "gpt-4o-mini", client: inner)]);

        ChatResponse response = await client.GetResponseAsync(messages, options, CancellationToken.None);

        Assert.Same(expectedResponse, response);

        Assert.Same(options, forwarded);
        Assert.Null(response.AdditionalProperties);
    }

    [Fact]
    public async Task Failover_HonorsExplicitModelId()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new OrderedFailoverRouter(
        [
            new ChatRoute("m1", modelId: "gpt-a", client: c1),
            new ChatRoute("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-b" });

        Assert.Same(r2, response);
    }

    [Fact]
    public async Task Failover_HonorsExplicitModelId_CaseInsensitively()
    {
        var r1 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "one"));
        var r2 = new ChatResponse(new ChatMessage(ChatRole.Assistant, "two"));

        using var c1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r1) };
        using var c2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(r2) };

        using var client = new OrderedFailoverRouter(
        [
            new ChatRoute("first", modelId: "gpt-a", client: c1),
            new ChatRoute("GPT-4o", modelId: "openai/GPT-4o", client: c2),
        ]);

        // Matches the second route by Name (case-insensitive), not the default first route.
        ChatResponse byName = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "gpt-4o" });
        Assert.Same(r2, byName);

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

        using var client = new OrderedFailoverRouter(
        [
            new ChatRoute("m1", modelId: "gpt-a", client: c1),
            new ChatRoute("m2", modelId: "gpt-b", client: c2),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = "does-not-exist" });

        Assert.Same(r1, response);
    }

    [Fact]
    public async Task Failover_WalksRoutesOnFailure()
    {
        var good = new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
        using var failing = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("boom") };
        using var working = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(good) };

        using var client = new OrderedFailoverRouter(
        [
            new ChatRoute("primary", client: failing),
            new ChatRoute("backup", client: working),
        ]);

        ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(good, response);
    }

    [Fact]
    public async Task Failover_Exhausted_PropagatesLastException()
    {
        using var failing1 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("a") };
        using var failing2 = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("b") };

        using var client = new OrderedFailoverRouter(
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

        using var client = new OrderedFailoverRouter(
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
    }

    [Fact]
    public async Task Dispatch_DoesNotInterpretRouteMetadata()
    {
        ChatOptions? forwarded = null;
        var options = new ChatOptions();
        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, o, _) => { forwarded = o; return Task.FromResult(new ChatResponse()); },
        };

        using var client = new OrderedFailoverRouter(
            [new ChatRoute("m1", modelId: "gpt-x", reasoningEffort: ReasoningEffort.High, client: inner)]);

        _ = await client.GetResponseAsync([new(ChatRole.User, "hi")], options);

        Assert.Same(options, forwarded);
        Assert.Null(forwarded!.ModelId);
        Assert.Null(options.Reasoning);
    }

    [Fact]
    public async Task Streaming_PassesUpdatesThroughUnchanged()
    {
        using var inner = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("a", "b") };

        using var client = new OrderedFailoverRouter([new ChatRoute("m1", modelId: "x", client: inner)]);

        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal(["a", "b"], updates.Select(u => u.Text));
        Assert.All(updates, u => Assert.Null(u.AdditionalProperties));
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
    public async Task Policy_CanDispatchRouteOutsideRegisteredSet()
    {
        int registeredCalls = 0;
        int selectedCalls = 0;
        using var registered = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                registeredCalls++;
                return Task.FromResult(new ChatResponse());
            },
        };
        using var selected = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                selectedCalls++;
                return Task.FromResult(new ChatResponse());
            },
        };
        var stranger = new ChatRoute("stranger", client: selected);

        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: registered)],
            (_, _, _, _, _) => stranger);

        _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(0, registeredCalls);
        Assert.Equal(1, selectedCalls);
    }

    [Fact]
    public async Task Policy_CanRetryAlreadyAttemptedRoute()
    {
        int attempts = 0;
        using var inner = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                attempts++;
                return attempts == 1
                    ? throw new InvalidOperationException("transient")
                    : Task.FromResult(new ChatResponse());
            },
        };

        using var client = new DelegatingTestRouter(
            [new ChatRoute("m1", client: inner)],
            (_, _, routes, _, _) => routes[0]);

        _ = await client.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(2, attempts);
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

        using var client = new OrderedFailoverRouter(
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
    public void Dispose_DisposesEachClientExactlyOnce()
    {
#pragma warning disable CA2000 // Dispose objects before losing scope
        var shared = new CountingDisposeClient();
        var other = new CountingDisposeClient();

        var client = new OrderedFailoverRouter(
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
    public async Task Messages_NonListEnumerable_IsPassedThroughUnchanged()
    {
        // The router does not enumerate or snapshot messages; the policy and dispatched client receive the caller's
        // original sequence and own any enumeration semantics.
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

        IEnumerable<ChatMessage> messages = Lazy();
        _ = await client.GetResponseAsync(messages);

        Assert.Equal(0, enumerations);
        Assert.Same(messages, policySaw);
        Assert.Same(messages, dispatchSaw);
    }

    [Fact]
    public async Task Nesting_ReturnsLeafResponseUnchanged()
    {
        var leafResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["leaf"] = true },
        };
        using var leafClient = new TestChatClient { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(leafResponse) };

        using var inner = new OrderedFailoverRouter(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new OrderedFailoverRouter(
            [new ChatRoute("Complexity", client: inner)]);

        ChatResponse response = await outer.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Same(leafResponse, response);
        AdditionalPropertiesDictionary props = Assert.IsType<AdditionalPropertiesDictionary>(response.AdditionalProperties);
        Assert.Single(props);
        Assert.True(Assert.IsType<bool>(props["leaf"]));
    }

    [Fact]
    public async Task Nesting_Streaming_ReturnsLeafUpdatesUnchanged()
    {
        using var leafClient = new TestChatClient { GetStreamingResponseAsyncCallback = (_, _, _) => YieldUpdates("ok") };
        using var inner = new OrderedFailoverRouter(
            [new ChatRoute("gpt-4o-mini", providerName: "openai", modelId: "openai/gpt-4o-mini", client: leafClient)]);
        using var outer = new OrderedFailoverRouter(
            [new ChatRoute("Complexity", client: inner)]);

        ChatResponseUpdate? first = null;
        await foreach (ChatResponseUpdate update in outer.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            first ??= update;
        }

        Assert.NotNull(first);
        Assert.Equal("ok", first!.Text);
        Assert.Null(first.AdditionalProperties);
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

        protected override ValueTask<ChatRoute?> SelectRouteAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            IReadOnlyList<ChatRoute> routes,
            IReadOnlyList<ChatRoute> attempted,
            Exception? lastException,
            CancellationToken cancellationToken) =>
            new(_select(messages, options, routes, attempted, lastException));
    }

    // A RoutingChatClient that implements ordered-failover selection: honor an explicit ModelId, otherwise the
    // first registered route, then each remaining route in registration order on failure. This is the canonical
    // ~15-line sample policy (the same one the docs show for "try routes in order until one works"); the tests
    // use it as a realistic subclass to exercise the base dispatch mechanism — dispatch, fallback, nesting, and
    // disposal.
    private sealed class OrderedFailoverRouter : RoutingChatClient
    {
        public OrderedFailoverRouter(IReadOnlyList<ChatRoute> routes)
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
            // Initial selection: honor an explicit ModelId, otherwise the first registered route.
            if (attempted.Count == 0)
            {
                return new(SelectInitialRoute(routes, options?.ModelId));
            }

            // Fallback: the next registered route not yet attempted, in registration order.
            foreach (ChatRoute route in routes)
            {
                if (!attempted.Contains(route))
                {
                    return new(route);
                }
            }

            // Every route attempted: stop and let the base rethrow the last exception.
            return new((ChatRoute?)null);
        }

        private static ChatRoute SelectInitialRoute(IReadOnlyList<ChatRoute> routes, string? modelId)
        {
            if (!string.IsNullOrEmpty(modelId))
            {
                foreach (ChatRoute route in routes)
                {
                    if (string.Equals(route.ModelId, modelId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(route.Name, modelId, StringComparison.OrdinalIgnoreCase))
                    {
                        return route;
                    }
                }
            }

            return routes[0];
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
