// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Illustrative cookbook sample. Not part of the build; see docs/routing-chat-client-cookbook.md.
#pragma warning disable MEAI001 // Routing types are experimental.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.AI.Samples.Routing;

/// <summary>
/// An embedding-based policy in the style of the semantic-router algorithm (which LiteLLM's "auto router"
/// delegates to): each route is described by a few representative "utterances", and a request is routed to the
/// route whose utterances are most similar to the last user message. For each request it embeds the query,
/// computes cosine similarity against every profile utterance, keeps the globally top-K matches, averages each
/// route's matched scores, and picks the highest-scoring route that clears a similarity threshold — otherwise a
/// default route. Profiles are embedded once and cached.
/// </summary>
/// <remarks>
/// On failure this sample falls back through the remaining routes in registration order. To fall back by
/// descending semantic score instead, cache the first-call ranking and walk it on later calls.
/// </remarks>
public sealed class SemanticRoutingClient : RoutingChatClient
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IReadOnlyDictionary<string, string[]> _profiles;
    private readonly string? _defaultRoute;
    private readonly int _topK;
    private readonly float _scoreThreshold;

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private (string Route, float[] Vector)[]? _index;

    /// <summary>Initializes the client with a per-route set of representative utterances.</summary>
    public SemanticRoutingClient(
        IReadOnlyList<ChatRoute> routes,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> routeProfiles,
        string? defaultRoute = null,
        int topK = 5,
        float scoreThreshold = 0.3f)
        : base(routes)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _ = routeProfiles ?? throw new ArgumentNullException(nameof(routeProfiles));
        _profiles = routeProfiles.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _defaultRoute = defaultRoute;
        _topK = topK;
        _scoreThreshold = scoreThreshold;
    }

    protected override async ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // Fallback: try the next untried route in registration order.
        if (attempted.Count > 0)
        {
            return routes.Except(attempted).FirstOrDefault();
        }

        string? query = LastUserText(messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            return DefaultRoute(routes);
        }

        (string Route, float[] Vector)[] index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        GeneratedEmbeddings<Embedding<float>> queryEmbedding =
            await _embeddings.GenerateAsync([query!], cancellationToken: cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<float> queryVector = queryEmbedding[0].Vector;

        var registered = routes.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Score every utterance belonging to a currently-registered route, then keep the global top-K.
        List<(string Route, double Score)> matches = index
            .Where(entry => registered.Contains(entry.Route))
            .Select(entry => (entry.Route, Score: (double)TensorPrimitives.CosineSimilarity(queryVector.Span, entry.Vector)))
            .OrderByDescending(m => m.Score)
            .Take(_topK)
            .ToList();

        // Average each route's surviving matches, then pick the best route that clears the threshold.
        string? winner = matches
            .GroupBy(m => m.Route, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Route: g.Key, Score: g.Average(m => m.Score)))
            .Where(g => g.Score >= _scoreThreshold)
            .OrderByDescending(g => g.Score)
            .Select(g => g.Route)
            .FirstOrDefault();

        return winner is null
            ? DefaultRoute(routes)
            : routes.FirstOrDefault(r => string.Equals(r.Name, winner, StringComparison.OrdinalIgnoreCase)) ?? DefaultRoute(routes);
    }

    private ChatRoute DefaultRoute(IReadOnlyList<ChatRoute> routes) =>
        (_defaultRoute is null ? null : routes.FirstOrDefault(r => string.Equals(r.Name, _defaultRoute, StringComparison.OrdinalIgnoreCase)))
        ?? routes[0];

    private static string? LastUserText(IEnumerable<ChatMessage> messages)
    {
        string? last = null;
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                last = message.Text;
            }
        }

        return last;
    }

    // Embeds every profile utterance once, lazily, and caches the flattened (route, vector) index.
    private async ValueTask<(string Route, float[] Vector)[]> EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is { } cached)
        {
            return cached;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is { } existing)
            {
                return existing;
            }

            var routeNames = new List<string>();
            var utterances = new List<string>();
            foreach (KeyValuePair<string, string[]> profile in _profiles)
            {
                foreach (string utterance in profile.Value)
                {
                    routeNames.Add(profile.Key);
                    utterances.Add(utterance);
                }
            }

            // The index is process-lifetime and shared, so embed it with None: one request cancelling must not
            // fault the cached index other requests await. (The per-request query embedding honors the caller's token.)
            GeneratedEmbeddings<Embedding<float>> embeddings =
                await _embeddings.GenerateAsync(utterances, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var index = new (string Route, float[] Vector)[utterances.Count];
            for (int i = 0; i < utterances.Count; i++)
            {
                index[i] = (routeNames[i], embeddings[i].Vector.ToArray());
            }

            _index = index;
            return index;
        }
        finally
        {
            _ = _indexGate.Release();
        }
    }
}
