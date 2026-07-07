// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class SemanticChatRouteSelectorTests
{
    [Fact]
    public void Ctor_NullEmbeddingGenerator_Throws()
    {
        var profiles = new Dictionary<string, IReadOnlyList<string>> { ["code"] = ["fix this bug"] };

        Assert.Throws<ArgumentNullException>(() => new SemanticChatRouteSelector(null!, profiles));
    }

    [Fact]
    public void Ctor_NullProfiles_Throws()
    {
        using var embedder = KeywordEmbeddingGenerator();

        Assert.Throws<ArgumentNullException>(() => new SemanticChatRouteSelector(embedder, null!));
    }

    [Fact]
    public void Ctor_EmptyProfiles_Throws()
    {
        using var embedder = KeywordEmbeddingGenerator();

        Assert.Throws<ArgumentException>(
            () => new SemanticChatRouteSelector(embedder, new Dictionary<string, IReadOnlyList<string>>()));
    }

    [Fact]
    public async Task RoutesToMostSimilarModel()
    {
        var models = new[]
        {
            new ChatRoute("code"),
            new ChatRoute("weather"),
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

        Assert.Equal("weather", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task EmbedsProfilesWithNoneToken_ButQueryWithCallerToken()
    {
        var models = new[]
        {
            new ChatRoute("code"),
            new ChatRoute("weather"),
        };

        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["weather"] = ["what is the weather like"],
            ["code"] = ["fix this bug in the code"],
        };

        var profileUtterances = new HashSet<string>(profiles.Values.SelectMany(v => v), StringComparer.Ordinal);
        CancellationToken profileToken = new(canceled: true); // sentinel: fails the assertion if the profile embed never runs
        CancellationToken? queryToken = null;

        using var embedder = new TestEmbeddingGenerator
        {
            GenerateAsyncCallback = (values, _, ct) =>
            {
                var list = values.ToList();

                // The one-shot profile embed passes every registered utterance; the per-request embed passes
                // just the (unknown) query string.
                if (list.Count > 0 && list.All(profileUtterances.Contains))
                {
                    profileToken = ct;
                }
                else
                {
                    queryToken = ct;
                }

                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (string value in list)
                {
                    result.Add(new Embedding<float>(KeywordVector(value)));
                }

                return Task.FromResult(result);
            }
        };

        var selector = new SemanticChatRouteSelector(embedder, profiles);
        using var requestCts = new CancellationTokenSource();
        var context = new ChatRouteContext([new(ChatRole.User, "is it going to rain today")], options: null, models);

        _ = await selector.SelectRouteAsync(context, requestCts.Token);

        // Process-lifetime profiles are embedded with a non-cancelable token so one request cancelling cannot
        // fault the single cached index that concurrent requests await.
        Assert.False(profileToken.CanBeCanceled);
        Assert.Equal(CancellationToken.None, profileToken);

        // The per-request query embedding still honors the caller's token.
        Assert.True(queryToken.HasValue);
        Assert.Equal(requestCts.Token, queryToken!.Value);
    }

    [Fact]
    public async Task FallsBackToFirstModelBelowMinimumSimilarity()
    {
        var models = new[]
        {
            new ChatRoute("code"),
            new ChatRoute("weather"),
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
        Assert.Equal("code", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task AggregatesWithMeanByDefault()
    {
        // "alpha" has one near-perfect utterance and one poor one (mean 0.50); "beta" has two
        // consistently strong utterances (mean 0.70). Mean aggregation (the default) prefers "beta".
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a-high", "a-low"],
            ["beta"] = ["b-mid1", "b-mid2"],
        };

        using var embedder = VectorEmbeddingGenerator(MeanVsMaxVectors());
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("beta", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task AggregatesWithMaxWhenConfigured()
    {
        // Same profiles as the mean test, but Max aggregation prefers "alpha" for its single best (0.95).
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
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

        Assert.Equal("alpha", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task AggregatesWithSumWhenConfigured()
    {
        // "alpha" has two moderate utterances (sum 0.80); "beta" has one strong one (sum 0.70). Sum
        // aggregation prefers "alpha" even though its mean (0.40) is lower than beta's (0.70).
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
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

        Assert.Equal("alpha", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task AppliesGlobalTopK()
    {
        // With full mean, "alpha" (0.6) beats "beta" (mean of 0.9 and 0.1 = 0.5). But TopK=1 keeps only
        // the single best match globally (beta's 0.9), so "beta" wins — proving top-k is global.
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
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

        Assert.Equal("beta", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task HonorsPerModelThreshold()
    {
        // "beta" scores highest (0.9) but its per-model threshold (0.95) rejects it, so the next model
        // past its threshold, "alpha" (0.6 >= global 0.3), is selected instead.
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
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
                ScoreThresholdByRoute = new Dictionary<string, float> { ["beta"] = 0.95f },
            });
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("alpha", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task RoutesToDefaultModelWithoutUserText()
    {
        var models = new[] { new ChatRoute("alpha"), new ChatRoute("beta") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = ["a1"],
            ["beta"] = ["b1"],
        };

        using var embedder = KeywordEmbeddingGenerator();
        var selector = new SemanticChatRouteSelector(embedder, profiles, defaultRoute: "beta");
        var context = new ChatRouteContext([new(ChatRole.System, "you are a helpful assistant")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        // No user message means no routing signal, so the configured default model is the primary route.
        Assert.Equal("beta", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task RecordsSemanticScoreInDecisionMetadata()
    {
        // The selector surfaces the winning model's aggregated similarity on ChatRoutePlan.DecisionMetadata,
        // which the router then projects onto the routing.decision telemetry event.
        var models = new[] { new ChatRoute("a"), new ChatRoute("b") };
        var profiles = new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["a-utt"], ["b"] = ["b-utt"] };
        var vectors = new Dictionary<string, float[]> { ["q"] = [1f, 0f], ["a-utt"] = [1f, 0f], ["b-utt"] = [0f, 1f] };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "q")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("a", plan.OrderedRoutes[0].Name);
        Assert.NotNull(plan.DecisionMetadata);
        Assert.True(plan.DecisionMetadata!.TryGetValue("routing.semantic.score", out object? score));
        Assert.Equal(1.0, Assert.IsType<double>(score), 3);
    }

    [Fact]
    public async Task RanksUnscoredModelsAsFallbacks()
    {
        // The plan ranks every registered model: the winner first, then scored models by descending score,
        // then models with no profile (unscored) in registration order — so lower-signal models act as fallbacks.
        var models = new[] { new ChatRoute("a"), new ChatRoute("b"), new ChatRoute("c") };
        var profiles = new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["a-utt"], ["b"] = ["b-utt"] };
        var vectors = new Dictionary<string, float[]>
        {
            ["route me"] = [1f, 0f],
            ["a-utt"] = [1f, 0f],       // cosine 1.0
            ["b-utt"] = [0.5f, 0.8660254f], // cosine 0.5
        };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal(["a", "b", "c"], plan.OrderedRoutes.Select(r => r.Name));
    }

    [Fact]
    public async Task IgnoresProfileForUnregisteredModel()
    {
        // A profile key that does not correspond to any registered route is silently ignored: its utterance,
        // even a perfect match, can never win, and the model never appears in the plan.
        var models = new[] { new ChatRoute("a"), new ChatRoute("b") };
        var profiles = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = ["a-utt"],
            ["b"] = ["b-utt"],
            ["ghost"] = ["g-utt"],
        };

        var vectors = new Dictionary<string, float[]>
        {
            ["route me"] = [1f, 0f],
            ["g-utt"] = [1f, 0f],            // perfect match, but "ghost" is not a registered route
            ["a-utt"] = [0.9f, 0.43588989f], // cosine 0.9
            ["b-utt"] = [0f, 1f],            // cosine 0.0
        };

        using var embedder = VectorEmbeddingGenerator(vectors);
        var selector = new SemanticChatRouteSelector(embedder, profiles);
        var context = new ChatRouteContext([new(ChatRole.User, "route me")], options: null, models);

        ChatRoutePlan plan = await selector.SelectRouteAsync(context);

        Assert.Equal("a", plan.OrderedRoutes[0].Name);
        Assert.Equal(2, plan.OrderedRoutes.Count);
        Assert.DoesNotContain(plan.OrderedRoutes, r => r.Name == "ghost");
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
}
