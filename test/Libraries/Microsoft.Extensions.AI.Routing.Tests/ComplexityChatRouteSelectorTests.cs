// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class ComplexityChatRouteSelectorTests
{
    private static readonly Dictionary<ChatComplexityTier, string> _tiers = new()
    {
        [ChatComplexityTier.Simple] = "mini",
        [ChatComplexityTier.Medium] = "standard",
        [ChatComplexityTier.Complex] = "pro",
        [ChatComplexityTier.Reasoning] = "reasoner",
    };

    private static readonly ChatRoute[] _routes =
    [
        new ChatRoute("mini"),
        new ChatRoute("standard"),
        new ChatRoute("pro"),
        new ChatRoute("reasoner"),
    ];

    [Fact]
    public void Ctor_NullMap_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ComplexityChatRouteSelector(null!));
    }

    [Fact]
    public void Ctor_EmptyMap_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ComplexityChatRouteSelector(new Dictionary<ChatComplexityTier, string>()));
    }

    [Fact]
    public void ClassifyTier_GreetingIsSimple()
    {
        var selector = new ComplexityChatRouteSelector(_tiers);

        Assert.Equal(ChatComplexityTier.Simple, selector.ClassifyTier([new(ChatRole.User, "hello there")]));
    }

    [Fact]
    public void ClassifyTier_TwoReasoningMarkersForceReasoning()
    {
        var selector = new ComplexityChatRouteSelector(_tiers);

        ChatComplexityTier tier = selector.ClassifyTier(
            [new(ChatRole.User, "Think step by step and analyze this carefully.")]);

        Assert.Equal(ChatComplexityTier.Reasoning, tier);
    }

    [Fact]
    public void ClassifyTier_CodeAndTechnicalIsComplex()
    {
        var selector = new ComplexityChatRouteSelector(_tiers);

        ChatComplexityTier tier = selector.ClassifyTier(
            [new(ChatRole.User, "Write a function for a distributed database api using encryption architecture.")]);

        Assert.Equal(ChatComplexityTier.Complex, tier);
    }

    [Fact]
    public async Task SelectRouteAsync_RoutesToMappedModelForTier()
    {
        var selector = new ComplexityChatRouteSelector(_tiers);
        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, _routes);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("mini", plan.OrderedRoutes[0].Name);
        Assert.Single(plan.OrderedRoutes);
    }

    [Fact]
    public async Task SelectRouteAsync_ResolvesTierRouteName_CaseInsensitively()
    {
        // The tier map value is a ChatRoute.Name; matching it must use the same case-insensitive rule the
        // router uses for name dedup, or a case-only mismatch silently falls through to Routes[0].
        var tiers = new Dictionary<ChatComplexityTier, string> { [ChatComplexityTier.Simple] = "MINI" };
        ChatRoute[] routes = [new ChatRoute("standard"), new ChatRoute("mini")];
        var selector = new ComplexityChatRouteSelector(tiers);
        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, routes);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        // Resolves to "mini" by case-insensitive name match, not the first route.
        Assert.Equal("mini", plan.OrderedRoutes[0].Name);
        Assert.Single(plan.OrderedRoutes);
    }

    [Fact]
    public async Task SelectRouteAsync_FallsBackToDefaultModel_WhenTierUnmapped()
    {
        var partial = new Dictionary<ChatComplexityTier, string> { [ChatComplexityTier.Complex] = "pro" };
        var selector = new ComplexityChatRouteSelector(partial, defaultRoute: "standard");
        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, _routes);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("standard", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task SelectRouteAsync_FallsBackToFirstModel_WhenTargetMissing()
    {
        var selector = new ComplexityChatRouteSelector(
            new Dictionary<ChatComplexityTier, string> { [ChatComplexityTier.Simple] = "absent" });
        var context = new ChatRouteContext([new(ChatRole.User, "hello there")], options: null, _routes);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("mini", plan.OrderedRoutes[0].Name);
        Assert.Single(plan.OrderedRoutes);
    }

    [Fact]
    public void ClassifyTier_CustomKeywordsChangeClassification()
    {
        // A non-short prompt (so the token signal is neutral) containing two nonsense words.
        ChatMessage[] messages = [new(ChatRole.User, "Please carefully frobnicate the wibble thoroughly and completely for me right away.")];

        // With default options these words are unknown, so the request scores as Simple.
        Assert.Equal(ChatComplexityTier.Simple, new ComplexityChatRouteSelector(_tiers).ClassifyTier(messages));

        // Treating them as code keywords (two matches reach the high bucket) lifts the score above Simple.
        var options = new ComplexityRouterOptions { CodeKeywords = ["frobnicate", "wibble"] };
        Assert.NotEqual(ChatComplexityTier.Simple, new ComplexityChatRouteSelector(_tiers, options: options).ClassifyTier(messages));
    }

    [Fact]
    public async Task SelectRouteAsync_RecordsComplexityTierInDecisionMetadata()
    {
        // The selector surfaces the classified tier on ChatRoutePlan.DecisionMetadata, which the router then
        // projects onto the routing.decision telemetry event.
        var selector = new ComplexityChatRouteSelector(_tiers);
        var context = new ChatRouteContext(
            [new(ChatRole.User, "Think step by step and analyze this carefully.")], options: null, _routes);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("reasoner", plan.OrderedRoutes[0].Name);
        Assert.NotNull(plan.DecisionMetadata);
        Assert.True(plan.DecisionMetadata!.TryGetValue("routing.complexity.tier", out object? tier));
        Assert.Equal("Reasoning", tier);
    }
}
