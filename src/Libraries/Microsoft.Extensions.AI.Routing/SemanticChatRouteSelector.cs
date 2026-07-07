// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// An embedding-based selection policy implementing the routing approach of Aurelio Labs' semantic-router
/// (the same algorithm LiteLLM's "auto router" delegates to):
/// each route is described by a small set of representative "utterances", and a request is routed to
/// the route whose utterances are most semantically similar to the last user message.
/// </summary>
/// <remarks>
/// <para>
/// This implements the routing math of Aurelio Labs' <c>semantic-router</c> (the library that LiteLLM's
/// "auto router" delegates to), using any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> as
/// the embedding backend. For each request it: embeds the last user message; computes cosine similarity
/// against every profile utterance; keeps the globally highest <see cref="SemanticRouterOptions.TopK"/>
/// matches; groups those matches by route; combines each route's matched scores with
/// <see cref="SemanticRouterOptions.Aggregation"/> (mean by default); then, in descending aggregated
/// score order, routes to the first route whose score meets its threshold
/// (<see cref="SemanticRouterOptions.ScoreThreshold"/>, or a per-route override). When no route meets
/// the threshold — or the request carries no user text — the optional <c>defaultRoute</c> (else the
/// first registered route) becomes the primary route.
/// </para>
/// <para>
/// Profiles are embedded once on first use and cached; wrap the injected generator with caching to also
/// amortize the per-request query embedding. Similarity is computed with
/// <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>. The
/// produced plan ranks all routes (primary first, the rest by descending score), so lower-scoring
/// routes act as fallbacks.
/// </para>
/// <para>
/// Profile keys are matched to registered routes by <see cref="ChatRoute.Name"/>
/// (case-insensitive). A profile whose key does not correspond to any route in the request's
/// <c>ChatRouteContext.Routes</c> is silently ignored — its utterances can never win — and there is no
/// diagnostic for such an unmatched key, so keep the profile keys in sync with the routes you register.
/// A registered route with no profile is only reachable as the default route or a plan fallback.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class SemanticChatRouteSelector : IChatRouteSelector
{
    // Decision-rationale key surfaced as a routing.decision tag (the pinned route's aggregated cosine
    // similarity). Kept private: the tag schema is a telemetry detail observed through ActivityListener.
    private const string SemanticScoreMetadataKey = "routing.semantic.score";

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly Dictionary<string, string[]> _routeProfiles;
    private readonly string? _defaultRoute;
    private readonly int _topK;
    private readonly SemanticRouteAggregation _aggregation;
    private readonly float _scoreThreshold;
    private readonly Dictionary<string, float>? _scoreThresholdByRoute;
    private readonly object _gate = new();

    private Task<ProfileIndex>? _profilesTask;

    /// <summary>Initializes a new instance of the <see cref="SemanticChatRouteSelector"/> class.</summary>
    /// <param name="embeddingGenerator">The embedding generator used to embed profiles and queries.</param>
    /// <param name="routeProfiles">A map from route name to representative utterances describing that route.</param>
    /// <param name="defaultRoute">
    /// The optional route name to route to when no route's aggregated score meets its threshold, or the
    /// request has no user text. When omitted, the first registered route is used.
    /// </param>
    /// <param name="options">The optional routing configuration. Defaults mirror the LiteLLM semantic router.</param>
    public SemanticChatRouteSelector(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IReadOnlyDictionary<string, IReadOnlyList<string>> routeProfiles,
        string? defaultRoute = null,
        SemanticRouterOptions? options = null)
    {
        _embeddingGenerator = Throw.IfNull(embeddingGenerator);
        _ = Throw.IfNull(routeProfiles);

        _routeProfiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IReadOnlyList<string>> profile in routeProfiles)
        {
            _ = Throw.IfNullOrWhitespace(profile.Key);
            if (profile.Value is null || profile.Value.Count == 0)
            {
                Throw.ArgumentException(nameof(routeProfiles), $"The profile for '{profile.Key}' must contain at least one utterance.");
            }

            var utterances = new string[profile.Value.Count];
            for (int i = 0; i < profile.Value.Count; i++)
            {
                utterances[i] = Throw.IfNullOrWhitespace(profile.Value[i], $"{nameof(routeProfiles)} utterance");
            }

            _routeProfiles[profile.Key] = utterances;
        }

        if (_routeProfiles.Count == 0)
        {
            Throw.ArgumentException(nameof(routeProfiles), "At least one route profile must be provided.");
        }

        _defaultRoute = defaultRoute;

        options ??= new SemanticRouterOptions();
        if (options.TopK < 1)
        {
            Throw.ArgumentException(nameof(options), $"{nameof(SemanticRouterOptions.TopK)} must be at least 1.");
        }

        _topK = options.TopK;
        _aggregation = options.Aggregation;
        _scoreThreshold = options.ScoreThreshold;
        if (options.ScoreThresholdByRoute is { Count: > 0 })
        {
            _scoreThresholdByRoute = options.ScoreThresholdByRoute.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        var ctx = Throw.IfNull(context);

        string? query = GetLastUserText(ctx.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            // Faithful to LiteLLM: with no user message there is nothing to route on, so fall back.
            return new ChatRoutePlan(OrderPlan(ctx.Routes, PrimaryFallback(ctx.Routes), scores: null));
        }

        Dictionary<string, double> aggregated = await ScoreRoutesAsync(query!, ctx.Routes, cancellationToken);

        string? winner = SelectWinner(aggregated, ctx.Routes);
        string primary = winner ?? PrimaryFallback(ctx.Routes);

        Dictionary<string, object>? metadata = null;
        if (aggregated.TryGetValue(primary, out double primaryScore))
        {
            metadata = new Dictionary<string, object>(StringComparer.Ordinal) { [SemanticScoreMetadataKey] = primaryScore };
        }

        return new ChatRoutePlan(OrderPlan(ctx.Routes, primary, aggregated), metadata);
    }

    // Orders the plan: the primary route first, then the remaining routes by descending aggregated
    // score (unscored routes keep registration order).
    private static ChatRoute[] OrderPlan(IReadOnlyList<ChatRoute> routes, string primary, Dictionary<string, double>? scores)
    {
        return routes
            .Select((route, index) => (
                Route: route,
                Index: index,
                IsPrimary: string.Equals(route.Name, primary, StringComparison.OrdinalIgnoreCase),
                Score: scores is not null && scores.TryGetValue(route.Name, out double score) ? score : double.NegativeInfinity))
            .OrderByDescending(entry => entry.IsPrimary)
            .ThenByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Route)
            .ToArray();
    }

    // Faithful to LiteLLM's _extract_text_from_messages: the last user message only (no fallback to
    // other roles). With no user message, routing has no signal.
    private static string? GetLastUserText(IEnumerable<ChatMessage> messages)
    {
        string? lastUser = null;
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                lastUser = message.Text;
            }
        }

        return lastUser;
    }

    // Embeds the query, scores every registered route's utterances by cosine similarity, keeps the globally
    // highest TopK matches, and reduces each route's matched scores with the configured aggregation.
    private async ValueTask<Dictionary<string, double>> ScoreRoutesAsync(string query, IReadOnlyList<ChatRoute> routes, CancellationToken cancellationToken)
    {
        ProfileIndex index = await EnsureProfilesAsync(cancellationToken);

        GeneratedEmbeddings<Embedding<float>> queryEmbedding =
            await _embeddingGenerator.GenerateAsync([query], cancellationToken: cancellationToken);
        ReadOnlyMemory<float> queryVector = queryEmbedding[0].Vector;

        // Only utterances belonging to a currently-registered route can win, mirroring how the router
        // can only route to a known deployment.
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatRoute route in routes)
        {
            _ = registered.Add(route.Name);
        }

        // Score every profile utterance with cosine similarity against the query.
        var matches = new List<Match>(index.Length);
        for (int i = 0; i < index.Length; i++)
        {
            string route = index.RouteNames[i];
            if (!registered.Contains(route))
            {
                continue;
            }

            double score = TensorPrimitives.CosineSimilarity(queryVector.Span, index.Vectors[i]);
            matches.Add(new Match(route, score, matches.Count));
        }

        // Keep only the globally highest TopK matches (semantic-router queries its index for top_k),
        // breaking ties by original order so selection is deterministic.
        matches.Sort(static (a, b) =>
        {
            int comparison = b.Score.CompareTo(a.Score);
            return comparison != 0 ? comparison : a.Order.CompareTo(b.Order);
        });

        int take = Math.Min(_topK, matches.Count);
        return AggregateTopMatches(matches, take, routes);
    }

    // Resolves the threshold a route must meet to be selected: its per-route override if present, else the
    // global threshold.
    private float ThresholdFor(string routeName)
    {
        if (_scoreThresholdByRoute is not null && _scoreThresholdByRoute.TryGetValue(routeName, out float perRoute))
        {
            return perRoute;
        }

        return _scoreThreshold;
    }

    // Groups the top `take` matches by route and reduces each route's scores with the configured
    // aggregation. Only the matches that survive the global top-k contribute, faithful to LiteLLM.
    private Dictionary<string, double> AggregateTopMatches(List<Match> matches, int take, IReadOnlyList<ChatRoute> routes)
    {
        var grouped = new Dictionary<string, (double Sum, double Max, int Count)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < take; i++)
        {
            Match match = matches[i];
            if (grouped.TryGetValue(match.Route, out (double Sum, double Max, int Count) current))
            {
                grouped[match.Route] = (current.Sum + match.Score, Math.Max(current.Max, match.Score), current.Count + 1);
            }
            else
            {
                grouped[match.Route] = (match.Score, match.Score, 1);
            }
        }

        // Key the result by the registered route's exact name so downstream ordering matches by identity.
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatRoute route in routes)
        {
            if (grouped.TryGetValue(route.Name, out (double Sum, double Max, int Count) stats))
            {
                result[route.Name] = _aggregation switch
                {
                    SemanticRouteAggregation.Sum => stats.Sum,
                    SemanticRouteAggregation.Max => stats.Max,
                    _ => stats.Sum / stats.Count,
                };
            }
        }

        return result;
    }

    // Iterates routes in descending aggregated score (ties by registration order) and returns the first
    // whose score meets its threshold (a per-route override, else the global threshold). Faithful to
    // semantic-router's _pass_routes, which sorts by score then returns the first route past threshold.
    private string? SelectWinner(Dictionary<string, double> aggregated, IReadOnlyList<ChatRoute> routes)
    {
        var ordered = new List<(string Name, double Score, int Order)>(aggregated.Count);
        for (int order = 0; order < routes.Count; order++)
        {
            if (aggregated.TryGetValue(routes[order].Name, out double score))
            {
                ordered.Add((routes[order].Name, score, order));
            }
        }

        ordered.Sort(static (a, b) =>
        {
            int comparison = b.Score.CompareTo(a.Score);
            return comparison != 0 ? comparison : a.Order.CompareTo(b.Order);
        });

        foreach ((string name, double score, _) in ordered)
        {
            if (score >= ThresholdFor(name))
            {
                return name;
            }
        }

        return null;
    }

    // The primary route used when no route passes its threshold: the configured default route if it is
    // registered, otherwise the first registered route.
    private string PrimaryFallback(IReadOnlyList<ChatRoute> routes)
    {
        if (_defaultRoute is not null)
        {
            foreach (ChatRoute route in routes)
            {
                if (string.Equals(route.Name, _defaultRoute, StringComparison.OrdinalIgnoreCase))
                {
                    return route.Name;
                }
            }
        }

        return routes[0].Name;
    }

    private Task<ProfileIndex> EnsureProfilesAsync(CancellationToken cancellationToken)
    {
        Task<ProfileIndex>? existing = Volatile.Read(ref _profilesTask);
        if (existing is not null && !existing.IsFaulted && !existing.IsCanceled)
        {
            return existing;
        }

        lock (_gate)
        {
            if (_profilesTask is null || _profilesTask.IsFaulted || _profilesTask.IsCanceled)
            {
                _profilesTask = EmbedProfilesAsync(cancellationToken);
            }

            return _profilesTask;
        }
    }

    private async Task<ProfileIndex> EmbedProfilesAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var utterances = new List<string>();
        foreach (KeyValuePair<string, string[]> profile in _routeProfiles)
        {
            foreach (string utterance in profile.Value)
            {
                names.Add(profile.Key);
                utterances.Add(utterance);
            }
        }

        // The profile index is process-lifetime and shared across every request, so it is embedded with
        // CancellationToken.None rather than the caller's token: a single request cancelling must not fault the
        // one cached index that concurrent requests are awaiting. (The per-request query embedding in
        // ScoreRoutesAsync still honors the caller's token.) A faulted embedding is not cached: EnsureProfilesAsync
        // re-initiates it on the next call. The parameter is retained for a symmetric signature but deliberately
        // not forwarded to the embedding call.
        _ = cancellationToken;
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await _embeddingGenerator.GenerateAsync(utterances, cancellationToken: CancellationToken.None);

        var vectors = new float[utterances.Count][];
        for (int i = 0; i < utterances.Count; i++)
        {
            vectors[i] = embeddings[i].Vector.ToArray();
        }

        return new ProfileIndex(names.ToArray(), vectors);
    }

    // A flattened index of every profile utterance: RouteNames[i] is the route that owns Vectors[i].
    private sealed class ProfileIndex
    {
        public ProfileIndex(string[] routeNames, float[][] vectors)
        {
            RouteNames = routeNames;
            Vectors = vectors;
        }

        public string[] RouteNames { get; }

        public float[][] Vectors { get; }

        public int Length => Vectors.Length;
    }

    private readonly struct Match
    {
        public Match(string route, double score, int order)
        {
            Route = route;
            Score = score;
            Order = order;
        }

        public string Route { get; }

        public double Score { get; }

        public int Order { get; }
    }
}
