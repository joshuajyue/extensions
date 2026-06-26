// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// An embedding-based selection policy. Each model is described by a small set of representative
/// "utterances"; at request time the last user message is embedded and routed to the model whose
/// utterances are most semantically similar.
/// </summary>
/// <remarks>
/// <para>
/// Profiles are embedded once on first use and cached. Similarity is computed with
/// <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>. Provide the
/// injected <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> wrapped with caching to amortize the
/// per-request embedding cost.
/// </para>
/// <para>
/// If the best similarity is below <c>minimumSimilarity</c>, or the request carries no user text, the
/// first registered model is used as the primary route. The produced plan ranks all models, so lower
/// scoring models act as fallbacks.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class SemanticChatRouteSelector : IChatRouteSelector
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly Dictionary<string, string[]> _modelProfiles;
    private readonly float _minimumSimilarity;
    private readonly object _gate = new();

    private Task<Dictionary<string, float[][]>>? _profilesTask;

    /// <summary>Initializes a new instance of the <see cref="SemanticChatRouteSelector"/> class.</summary>
    /// <param name="embeddingGenerator">The embedding generator used to embed profiles and queries.</param>
    /// <param name="modelProfiles">A map from model name to representative utterances describing that model.</param>
    /// <param name="minimumSimilarity">
    /// The minimum cosine similarity required to route to a profiled model. When the best score is below
    /// this value, the first registered model is used as the primary route. Defaults to 0.
    /// </param>
    public SemanticChatRouteSelector(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IReadOnlyDictionary<string, IReadOnlyList<string>> modelProfiles,
        float minimumSimilarity = 0f)
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

        _minimumSimilarity = minimumSimilarity;
    }

    /// <inheritdoc/>
    public async ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        var ctx = Throw.IfNull(context);

        string? query = GetLastUserText(ctx.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            // No signal to route on; preserve registration order.
            return new ChatRoutePlan(ctx.Models);
        }

        Dictionary<string, float[][]> profiles = await EnsureProfilesAsync(cancellationToken);

        GeneratedEmbeddings<Embedding<float>> queryEmbedding =
            await _embeddingGenerator.GenerateAsync([query!], cancellationToken: cancellationToken);
        ReadOnlyMemory<float> queryVector = queryEmbedding[0].Vector;

        var scored = new List<(RoutingChatModel Model, int Order, double Score)>(ctx.Models.Count);
        for (int order = 0; order < ctx.Models.Count; order++)
        {
            RoutingChatModel model = ctx.Models[order];
            double best = double.NegativeInfinity;
            if (profiles.TryGetValue(model.Name, out float[][]? vectors))
            {
                foreach (float[] vector in vectors)
                {
                    double similarity = TensorPrimitives.CosineSimilarity(queryVector.Span, vector);
                    if (similarity > best)
                    {
                        best = similarity;
                    }
                }
            }

            scored.Add((model, order, best));
        }

        // Highest score first; ties preserve registration order.
        scored.Sort(static (a, b) =>
        {
            int comparison = b.Score.CompareTo(a.Score);
            return comparison != 0 ? comparison : a.Order.CompareTo(b.Order);
        });

        var ranked = new List<RoutingChatModel>(scored.Count);
        foreach ((RoutingChatModel model, _, _) in scored)
        {
            ranked.Add(model);
        }

        if (scored.Count > 0 && scored[0].Score < _minimumSimilarity)
        {
            // No sufficiently similar model: fall back to the first registered model as primary.
            RoutingChatModel primary = ctx.Models[0];
            _ = ranked.Remove(primary);
            ranked.Insert(0, primary);
        }

        return new ChatRoutePlan(ranked);
    }

    private static string? GetLastUserText(IEnumerable<ChatMessage> messages)
    {
        string? lastUser = null;
        string? last = null;
        foreach (ChatMessage message in messages)
        {
            last = message.Text;
            if (message.Role == ChatRole.User)
            {
                lastUser = message.Text;
            }
        }

        return lastUser ?? last;
    }

    private Task<Dictionary<string, float[][]>> EnsureProfilesAsync(CancellationToken cancellationToken)
    {
        Task<Dictionary<string, float[][]>>? existing = Volatile.Read(ref _profilesTask);
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

    private async Task<Dictionary<string, float[][]>> EmbedProfilesAsync(CancellationToken cancellationToken)
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

        var accumulated = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < utterances.Count; i++)
        {
            if (!accumulated.TryGetValue(names[i], out List<float[]>? list))
            {
                list = [];
                accumulated[names[i]] = list;
            }

            list.Add(embeddings[i].Vector.ToArray());
        }

        var result = new Dictionary<string, float[][]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<float[]>> entry in accumulated)
        {
            result[entry.Key] = [.. entry.Value];
        }

        return result;
    }
}
