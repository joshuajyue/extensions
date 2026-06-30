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
/// An embedding-based selection policy that mirrors the LiteLLM "auto router" (its semantic router):
/// each model is described by a small set of representative "utterances", and a request is routed to
/// the model whose utterances are most semantically similar to the last user message.
/// </summary>
/// <remarks>
/// <para>
/// This is a faithful port of LiteLLM's semantic router (which delegates the routing math to the
/// <c>semantic-router</c> library), using any <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> as
/// the embedding backend. For each request it: embeds the last user message; computes cosine similarity
/// against every profile utterance; keeps the globally highest <see cref="SemanticRouterOptions.TopK"/>
/// matches; groups those matches by model; combines each model's matched scores with
/// <see cref="SemanticRouterOptions.Aggregation"/> (mean by default); then, in descending aggregated
/// score order, routes to the first model whose score meets its threshold
/// (<see cref="SemanticRouterOptions.ScoreThreshold"/>, or a per-model override). When no model meets
/// the threshold — or the request carries no user text — the optional <c>defaultModel</c> (else the
/// first registered model) becomes the primary route.
/// </para>
/// <para>
/// Profiles are embedded once on first use and cached; wrap the injected generator with caching to also
/// amortize the per-request query embedding. Similarity is computed with
/// <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>. The
/// produced plan ranks all models (primary first, the rest by descending score), so lower-scoring
/// models act as fallbacks.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class SemanticChatRouteSelector : IChatRouteSelector
{
    // Decision-rationale key surfaced as a routing.decision tag (the pinned model's aggregated cosine
    // similarity). Kept private: the tag schema is a telemetry detail observed through ActivityListener.
    private const string SemanticScoreMetadataKey = "routing.semantic.score";

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly Dictionary<string, string[]> _modelProfiles;
    private readonly string? _defaultModel;
    private readonly int _topK;
    private readonly SemanticRouteAggregation _aggregation;
    private readonly float _scoreThreshold;
    private readonly Dictionary<string, float>? _scoreThresholdByModel;
    private readonly bool _reselectBelowThreshold;
    private readonly object _gate = new();

    private Task<ProfileIndex>? _profilesTask;

    /// <summary>Initializes a new instance of the <see cref="SemanticChatRouteSelector"/> class.</summary>
    /// <param name="embeddingGenerator">The embedding generator used to embed profiles and queries.</param>
    /// <param name="modelProfiles">A map from model name to representative utterances describing that model.</param>
    /// <param name="defaultModel">
    /// The optional model name to route to when no model's aggregated score meets its threshold, or the
    /// request has no user text. When omitted, the first registered model is used.
    /// </param>
    /// <param name="options">The optional routing configuration. Defaults mirror the LiteLLM semantic router.</param>
    public SemanticChatRouteSelector(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelProfiles,
        string? defaultModel = null,
        SemanticRouterOptions? options = null)
    {
        _embeddingGenerator = Throw.IfNull(embeddingGenerator);
        _ = Throw.IfNull(modelProfiles);

        _modelProfiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IReadOnlyList<string>> profile in modelProfiles)
        {
            _ = Throw.IfNullOrWhitespace(profile.Key);
            if (profile.Value is null || profile.Value.Count == 0)
            {
                Throw.ArgumentException(nameof(modelProfiles), $"The profile for '{profile.Key}' must contain at least one utterance.");
            }

            var utterances = new string[profile.Value.Count];
            for (int i = 0; i < profile.Value.Count; i++)
            {
                utterances[i] = Throw.IfNullOrWhitespace(profile.Value[i], $"{nameof(modelProfiles)} utterance");
            }

            _modelProfiles[profile.Key] = utterances;
        }

        if (_modelProfiles.Count == 0)
        {
            Throw.ArgumentException(nameof(modelProfiles), "At least one model profile must be provided.");
        }

        _defaultModel = defaultModel;

        options ??= new SemanticRouterOptions();
        if (options.TopK < 1)
        {
            Throw.ArgumentException(nameof(options), $"{nameof(SemanticRouterOptions.TopK)} must be at least 1.");
        }

        _topK = options.TopK;
        _aggregation = options.Aggregation;
        _scoreThreshold = options.ScoreThreshold;
        _reselectBelowThreshold = options.ReselectBelowThreshold;
        if (options.ScoreThresholdByModel is { Count: > 0 })
        {
            _scoreThresholdByModel = options.ScoreThresholdByModel.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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
            return new ChatRoutePlan(OrderPlan(ctx.Models, PrimaryFallback(ctx.Models), scores: null));
        }

        Dictionary<string, double> aggregated = await ScoreModelsAsync(query!, ctx.Models, cancellationToken);

        string? winner = SelectWinner(aggregated, ctx.Models);
        string primary = winner ?? PrimaryFallback(ctx.Models);

        Dictionary<string, object>? metadata = null;
        if (aggregated.TryGetValue(primary, out double primaryScore))
        {
            metadata = new Dictionary<string, object>(StringComparer.Ordinal) { [SemanticScoreMetadataKey] = primaryScore };
        }

        // Under a sticky scope, the optional confidence floor keeps reusing this decision while the pinned model
        // still meets its selection threshold for later turns, re-routing once the conversation drifts below it.
        // Only a decision that won by passing its threshold gets a floor; a threshold-miss fallback stays put.
        Func<ChatRouteContext, CancellationToken, ValueTask<bool>>? remainsValid = null;
        if (_reselectBelowThreshold && winner is not null)
        {
            string pinnedModel = winner;
            float floor = ThresholdFor(pinnedModel);
            remainsValid = async (newContext, ct) =>
            {
                string? newQuery = GetLastUserText(newContext.Messages);
                if (string.IsNullOrWhiteSpace(newQuery))
                {
                    return true;
                }

                Dictionary<string, double> rescored = await ScoreModelsAsync(newQuery!, newContext.Models, ct);
                return rescored.TryGetValue(pinnedModel, out double score) && score >= floor;
            };
        }

        return new ChatRoutePlan(OrderPlan(ctx.Models, primary, aggregated), remainsValid, metadata);
    }

    // Orders the plan: the primary model first, then the remaining models by descending aggregated
    // score (unscored models keep registration order).
    private static RoutingChatModel[] OrderPlan(IReadOnlyList<RoutingChatModel> models, string primary, Dictionary<string, double>? scores)
    {
        return models
            .Select((model, index) => (
                Model: model,
                Index: index,
                IsPrimary: string.Equals(model.Name, primary, StringComparison.OrdinalIgnoreCase),
                Score: scores is not null && scores.TryGetValue(model.Name, out double score) ? score : double.NegativeInfinity))
            .OrderByDescending(entry => entry.IsPrimary)
            .ThenByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Model)
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

    // Embeds the query, scores every registered model's utterances by cosine similarity, keeps the globally
    // highest TopK matches, and reduces each model's matched scores with the configured aggregation. Shared by
    // the initial selection and the optional confidence-floor revalidation so both compute scores identically.
    private async ValueTask<Dictionary<string, double>> ScoreModelsAsync(string query, IReadOnlyList<RoutingChatModel> models, CancellationToken cancellationToken)
    {
        ProfileIndex index = await EnsureProfilesAsync(cancellationToken);

        GeneratedEmbeddings<Embedding<float>> queryEmbedding =
            await _embeddingGenerator.GenerateAsync([query], cancellationToken: cancellationToken);
        ReadOnlyMemory<float> queryVector = queryEmbedding[0].Vector;

        // Only utterances belonging to a currently-registered model can win, mirroring how the router
        // can only route to a known deployment.
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingChatModel model in models)
        {
            _ = registered.Add(model.Name);
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
        return AggregateTopMatches(matches, take, models);
    }

    // Resolves the threshold a model must meet to be selected: its per-model override if present, else the
    // global threshold. This same threshold is reused as the sticky confidence floor.
    private float ThresholdFor(string modelName)
    {
        if (_scoreThresholdByModel is not null && _scoreThresholdByModel.TryGetValue(modelName, out float perModel))
        {
            return perModel;
        }

        return _scoreThreshold;
    }

    // Groups the top `take` matches by model and reduces each model's scores with the configured
    // aggregation. Only the matches that survive the global top-k contribute, faithful to LiteLLM.
    private Dictionary<string, double> AggregateTopMatches(List<Match> matches, int take, IReadOnlyList<RoutingChatModel> models)
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

        // Key the result by the registered model's exact name so downstream ordering matches by identity.
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingChatModel model in models)
        {
            if (grouped.TryGetValue(model.Name, out (double Sum, double Max, int Count) stats))
            {
                result[model.Name] = _aggregation switch
                {
                    SemanticRouteAggregation.Sum => stats.Sum,
                    SemanticRouteAggregation.Max => stats.Max,
                    _ => stats.Sum / stats.Count,
                };
            }
        }

        return result;
    }

    // Iterates models in descending aggregated score (ties by registration order) and returns the first
    // whose score meets its threshold (a per-model override, else the global threshold). Faithful to
    // semantic-router's _pass_routes, which sorts by score then returns the first route past threshold.
    private string? SelectWinner(Dictionary<string, double> aggregated, IReadOnlyList<RoutingChatModel> models)
    {
        var ordered = new List<(string Name, double Score, int Order)>(aggregated.Count);
        for (int order = 0; order < models.Count; order++)
        {
            if (aggregated.TryGetValue(models[order].Name, out double score))
            {
                ordered.Add((models[order].Name, score, order));
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

    // The primary route used when no model passes its threshold: the configured default model if it is
    // registered, otherwise the first registered model.
    private string PrimaryFallback(IReadOnlyList<RoutingChatModel> models)
    {
        if (_defaultModel is not null)
        {
            foreach (RoutingChatModel model in models)
            {
                if (string.Equals(model.Name, _defaultModel, StringComparison.OrdinalIgnoreCase))
                {
                    return model.Name;
                }
            }
        }

        return models[0].Name;
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
        foreach (KeyValuePair<string, string[]> profile in _modelProfiles)
        {
            foreach (string utterance in profile.Value)
            {
                names.Add(profile.Key);
                utterances.Add(utterance);
            }
        }

        // A faulted or canceled embedding is not cached: EnsureProfilesAsync re-initiates it on the next call.
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await _embeddingGenerator.GenerateAsync(utterances, cancellationToken: cancellationToken);

        var vectors = new float[utterances.Count][];
        for (int i = 0; i < utterances.Count; i++)
        {
            vectors[i] = embeddings[i].Vector.ToArray();
        }

        return new ProfileIndex(names.ToArray(), vectors);
    }

    // A flattened index of every profile utterance: RouteNames[i] is the model that owns Vectors[i].
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
