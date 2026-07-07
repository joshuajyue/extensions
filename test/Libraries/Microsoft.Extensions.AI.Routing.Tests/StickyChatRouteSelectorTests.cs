// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class StickyChatRouteSelectorTests
{
    [Fact]
    public void Ctor_NullGetPins_Throws()
    {
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

        Assert.Throws<ArgumentNullException>(() => new StickyChatRouteSelector(null!, inner));
    }

    [Fact]
    public void Ctor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StickyChatRouteSelector(_ => null, null!));
    }

    [Fact]
    public async Task HoldsPin_EvenWhenInnerWouldChooseAnother()
    {
        ChatRoute[] routes = [new("fast"), new("smart")];
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "smart")));
        var sticky = new StickyChatRouteSelector(_ => ["fast"], inner);

        ChatRoutePlan plan = await sticky.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal("fast", plan.OrderedRoutes[0].Name);
        Assert.True(plan.DecisionMetadata?.ContainsKey(StickyChatRouteSelector.PinnedDecisionKey));
    }

    [Fact]
    public async Task NoPin_DefersToInner()
    {
        ChatRoute[] routes = [new("fast"), new("smart")];
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "smart")));
        var sticky = new StickyChatRouteSelector(_ => null, inner);

        ChatRoutePlan plan = await sticky.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal("smart", plan.OrderedRoutes[0].Name);
        Assert.Null(plan.DecisionMetadata);
    }

    [Fact]
    public async Task UnknownPinName_DefersToInner()
    {
        ChatRoute[] routes = [new("fast"), new("smart")];
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "smart")));
        var sticky = new StickyChatRouteSelector(_ => ["ghost"], inner);

        ChatRoutePlan plan = await sticky.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal("smart", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task MultiplePins_PreserveOrderAndDeduplicate()
    {
        // Pins are resolved in the callback's order; a repeated or unresolvable name is dropped without disturbing
        // the rest, so the plan is the de-duplicated, order-preserving set of resolved routes.
        ChatRoute[] routes = [new("fast"), new("smart")];
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "fast")));
        var sticky = new StickyChatRouteSelector(_ => ["smart", "ghost", "fast", "smart"], inner);

        ChatRoutePlan plan = await sticky.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal(["smart", "fast"], plan.OrderedRoutes.Select(r => r.Name));
    }

    [Fact]
    public async Task LayeredOverSemantic_HoldsFirstPick()
    {
        // Stickiness layered over the built-in SemanticChatRouteSelector: the first turn is routed by embedding
        // similarity; the app pins that pick, and later turns reuse it even when the content would otherwise
        // route elsewhere (no re-embedding decision, stable model for the conversation).
        ChatRoute[] routes = [new("weatherbot"), new("codebot")];
        using var embedder = new TestEmbeddingGenerator
        {
            GenerateAsyncCallback = (values, _, _) => Task.FromResult(Embed(values)),
        };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["weatherbot"] = ["today's weather forecast"],
            ["codebot"] = ["write some code"],
        };
        var semantic = new SemanticChatRouteSelector(embedder, profiles);

        string? pinned = null;
        var sticky = new StickyChatRouteSelector(_ => pinned is null ? null : [pinned], semantic);

        // Turn 1: not pinned yet — routed semantically to weatherbot; the app captures the pick.
        ChatRoutePlan turn1 = await sticky.SelectRouteAsync(Context(routes, "what is the weather today"));
        pinned = turn1.OrderedRoutes[0].Name;
        Assert.Equal("weatherbot", pinned);

        // Turn 2: content that WOULD route to codebot, but the pin holds weatherbot.
        ChatRoutePlan turn2 = await sticky.SelectRouteAsync(Context(routes, "help me write code"));
        Assert.Equal("weatherbot", turn2.OrderedRoutes[0].Name);

        // Control: without the pin, the same turn-2 content diverges to codebot.
        ChatRoutePlan control = await semantic.SelectRouteAsync(Context(routes, "help me write code"));
        Assert.Equal("codebot", control.OrderedRoutes[0].Name);
    }

    private static ChatRouteContext Context(IReadOnlyList<ChatRoute> routes, string userText) =>
        new([new(ChatRole.User, userText)], options: null, routes);

    private static ChatRoute Route(ChatRouteContext context, string name) =>
        context.Routes.First(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private static GeneratedEmbeddings<Embedding<float>> Embed(IEnumerable<string> values)
    {
        // Deterministic one-hot "embedding": weather -> x, code -> y, otherwise z. Cosine similarity is 1.0 for a
        // matching axis and 0.0 otherwise, so routing is fully predictable without a real embedding backend.
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (string value in values)
        {
            string upper = value.ToUpperInvariant();
            float[] vector;
            if (upper.Contains("WEATHER", StringComparison.Ordinal))
            {
                vector = [1f, 0f, 0f];
            }
            else if (upper.Contains("CODE", StringComparison.Ordinal))
            {
                vector = [0f, 1f, 0f];
            }
            else
            {
                vector = [0f, 0f, 1f];
            }

            result.Add(new Embedding<float>(vector));
        }

        return result;
    }
}
