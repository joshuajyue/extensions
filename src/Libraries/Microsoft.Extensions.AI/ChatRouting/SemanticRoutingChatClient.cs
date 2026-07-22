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

/// <summary>Routes requests by semantic similarity to app-provided example utterances.</summary>
/// <remarks>
/// <para>
/// Profile embeddings are generated lazily and cached. Each request embeds the last user message and selects the
/// client whose profile has the highest cosine similarity. The configured default client is selected when no user
/// message is available or when the highest score is below the configured threshold.
/// </para>
/// <para>
/// The configured clients are used as stable routing identities. By default this instance owns the clients and
/// embedding generator and disposes them when it is disposed.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class SemanticRoutingChatClient : RoutingChatClient
{
    private readonly IChatClient[] _clients;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly bool _leaveOpen;
    private readonly (IChatClient Client, string Text)[] _profiles;
    private readonly float _scoreThreshold;

    private bool _disposed;
    private EmbeddedProfile[]? _index;

    /// <summary>Initializes a new instance of the <see cref="SemanticRoutingChatClient"/> class.</summary>
    /// <param name="embeddingGenerator">The generator used to embed profile utterances and request text.</param>
    /// <param name="clientProfiles">The example utterances associated with each client.</param>
    /// <param name="defaultClient">The client selected when no profile satisfies <paramref name="scoreThreshold"/>.</param>
    /// <param name="scoreThreshold">The minimum cosine similarity required to select a profiled client.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave the configured clients and embedding generator open when this instance is
    /// disposed; otherwise, <see langword="false"/>. The default is <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="embeddingGenerator"/>, <paramref name="clientProfiles"/>, or
    /// <paramref name="defaultClient"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="clientProfiles"/> is empty or contains a null client, an empty utterance list, or a blank
    /// utterance.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scoreThreshold"/> is not between -1 and 1, inclusive.
    /// </exception>
    public SemanticRoutingChatClient(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IReadOnlyDictionary<IChatClient, IReadOnlyList<string>> clientProfiles,
        IChatClient defaultClient,
        float scoreThreshold = 0.3f,
        bool leaveOpen = false)
    {
        _embeddingGenerator = Throw.IfNull(embeddingGenerator);
        _ = Throw.IfNull(clientProfiles);
        _ = Throw.IfNull(defaultClient);
        _leaveOpen = leaveOpen;

        if (clientProfiles.Count == 0)
        {
            Throw.ArgumentException(nameof(clientProfiles), "At least one client profile must be provided.");
        }

        if (float.IsNaN(scoreThreshold) || float.IsInfinity(scoreThreshold) || scoreThreshold is < -1 or > 1)
        {
            Throw.ArgumentOutOfRangeException(nameof(scoreThreshold));
        }

        _scoreThreshold = scoreThreshold;

        var profiles = new List<(IChatClient Client, string Text)>();
        var clients = new List<IChatClient> { defaultClient };
        foreach (KeyValuePair<IChatClient, IReadOnlyList<string>> profile in clientProfiles)
        {
            IChatClient client = profile.Key;
            IReadOnlyList<string> utterances = profile.Value;
            if (client is null)
            {
                Throw.ArgumentException(nameof(clientProfiles), "Profile clients must not be null.");
            }

            if (utterances is null || utterances.Count == 0)
            {
                Throw.ArgumentException(
                    nameof(clientProfiles),
                    "Every profile client must have at least one example utterance.");
            }

            if (!clients.Exists(candidate => ReferenceEquals(candidate, client)))
            {
                clients.Add(client);
            }

            foreach (string utterance in utterances)
            {
                if (string.IsNullOrWhiteSpace(utterance))
                {
                    Throw.ArgumentException(nameof(clientProfiles), "Profile utterances must not be blank.");
                }

                profiles.Add((client, utterance));
            }
        }

        _clients = [.. clients];
        _profiles = [.. profiles];
    }

    /// <inheritdoc/>
    protected override async ValueTask<IChatClient> SelectClientAsync(
        RoutingContext context,
        CancellationToken cancellationToken)
    {
        _ = Throw.IfNull(context);
        string? query = LastUserText(context.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            return _clients[0];
        }

        EmbeddedProfile[] index = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        GeneratedEmbeddings<Embedding<float>> generated =
            await _embeddingGenerator.GenerateAsync(
                [query!],
                cancellationToken: cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("The embedding generator returned null.");
        if (generated.Count != 1)
        {
            throw new InvalidOperationException("The embedding generator did not return one query embedding.");
        }

        ReadOnlySpan<float> queryVector = generated[0].Vector.Span;
        if (queryVector.Length != index[0].Vector.Length)
        {
            throw new InvalidOperationException(
                "The query embedding dimension does not match the profile embedding dimension.");
        }

        IChatClient? bestClient = null;
        float bestScore = float.NegativeInfinity;
        foreach (EmbeddedProfile profile in index)
        {
            float score = TensorPrimitives.CosineSimilarity(queryVector, profile.Vector);
            if (score > bestScore)
            {
                bestClient = profile.Client;
                bestScore = score;
            }
        }

        return bestClient is not null && bestScore >= _scoreThreshold
            ? bestClient
            : _clients[0];
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (disposing)
            {
                _indexGate.Dispose();
                if (!_leaveOpen)
                {
                    foreach (IChatClient client in _clients)
                    {
                        client.Dispose();
                    }

                    if (!Array.Exists(
                        _clients,
                        client => ReferenceEquals(client, _embeddingGenerator)))
                    {
                        _embeddingGenerator.Dispose();
                    }
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

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

    private async Task<EmbeddedProfile[]> EnsureIndexAsync(CancellationToken cancellationToken)
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

            GeneratedEmbeddings<Embedding<float>> embeddings =
                await _embeddingGenerator.GenerateAsync(
                    _profiles.Select(profile => profile.Text),
                    cancellationToken: cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("The embedding generator returned null.");
            if (embeddings.Count != _profiles.Length)
            {
                throw new InvalidOperationException(
                    "The embedding generator did not return one embedding per profile utterance.");
            }

            int dimensions = embeddings[0].Vector.Length;
            if (dimensions == 0)
            {
                throw new InvalidOperationException("Profile embeddings must not be empty.");
            }

            var index = new EmbeddedProfile[_profiles.Length];
            for (int i = 0; i < index.Length; i++)
            {
                if (embeddings[i].Vector.Length != dimensions)
                {
                    throw new InvalidOperationException(
                        "All profile embeddings must have the same dimension.");
                }

                index[i] = new(_profiles[i].Client, embeddings[i].Vector.ToArray());
            }

            return _index = index;
        }
        finally
        {
            _ = _indexGate.Release();
        }
    }

    private sealed record EmbeddedProfile(IChatClient Client, float[] Vector);
}
