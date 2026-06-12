// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A simple multi-model chat router used as a step-1 implementation for auto-selection experiments.
/// </summary>
/// <remarks>
/// This type intentionally keeps routing logic minimal: it selects a candidate and forwards the call.
/// More advanced policy, telemetry, and fallback behavior can be layered later.
/// </remarks>
public class AutoSelectingChatClient : DelegatingChatClient
{
    // Keys used to annotate the selected candidate in ChatOptions.AdditionalProperties
    internal const string SelectedCandidateNameKey = "auto_select.candidate_name";
    internal const string SelectedProviderNameKey = "auto_select.provider_name";
    internal const string SelectedModelIdKey = "auto_select.model_id";

    // List of candidates
    private readonly AutoSelectingChatClientCandidate[] _candidates;

    // Selector function either provided by the user, or defaulting to a built-in selector.
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<AutoSelectingChatClientCandidate>, AutoSelectingChatClientCandidate> _selector;

    /// <summary>
    /// Validates the provided candidates and returns an array of non-null candidates.
    /// </summary>
    /// <param name="candidates">The list of candidates to validate.</param>
    /// <returns>An array of validated candidates.</returns>
    private static AutoSelectingChatClientCandidate[] ValidateCandidates(IReadOnlyList<AutoSelectingChatClientCandidate> candidates)
    {
        _ = Throw.IfNull(candidates);

        if (candidates.Count == 0)
        {
            Throw.ArgumentException(nameof(candidates), "At least one candidate client must be provided.");
        }

        var result = new AutoSelectingChatClientCandidate[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            result[i] = Throw.IfNull(candidates[i]);
        }

        return result;
    }

    /// <summary>
    /// Creates a normalized ChatOptions instance based on the provided options and the selected candidates.
    /// </summary>
    /// <param name="candidate">The selected candidate.</param>
    /// <param name="options">The original chat options.</param>
    /// <returns>A normalized ChatOptions instance.</returns>
    private static ChatOptions CreateNormalizedOptions(AutoSelectingChatClientCandidate candidate, ChatOptions? options)
    {
        ChatOptions normalized = options?.Clone() ?? new ChatOptions();

        if (normalized.ModelId is null && candidate.ModelId is not null)
        {
            normalized.ModelId = candidate.ModelId;
        }

        AdditionalPropertiesDictionary metadata = normalized.AdditionalProperties ??= [];
        metadata[SelectedCandidateNameKey] = candidate.Name;
        metadata[SelectedProviderNameKey] = candidate.ProviderName;
        metadata[SelectedModelIdKey] = normalized.ModelId;

        return normalized;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoSelectingChatClient"/> class with the specified candidates and an optional selector function.
    /// </summary>
    /// <param name="candidates">The list of candidate chat clients.</param>
    /// <param name="selector">An optional selector function to choose a candidate.</param>
    public AutoSelectingChatClient(
        IReadOnlyList<AutoSelectingChatClientCandidate> candidates,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<AutoSelectingChatClientCandidate>, AutoSelectingChatClientCandidate>? selector = null)
        : base(ValidateCandidates(candidates)[0].Client)
    {
        _candidates = ValidateCandidates(candidates);
        _selector = selector ?? ((_, _, validCandidates) => validCandidates[0]);
    }

    /// <summary>
    /// Selects a candidate chat client based on the provided messages and options, and forwards the request to the selected client's GetResponseAsync method.
    /// </summary>
    /// <param name="messages">The chat messages to send.</param>
    /// <param name="options">The chat options to use.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation, with a ChatResponse result.</returns>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        AutoSelectingChatClientCandidate candidate = SelectCandidate(messages, options);
        ChatOptions normalizedOptions = CreateNormalizedOptions(candidate, options);
        return candidate.Client.GetResponseAsync(messages, normalizedOptions, cancellationToken);
    }

    /// <summary>
    /// Selects a candidate chat client based on the provided messages and options, and forwards the request to the selected client's GetStreamingResponseAsync method.
    /// </summary>
    /// <param name="messages">The chat messages to send.</param>
    /// <param name="options">The chat options to use.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous stream of <see cref="ChatResponseUpdate"/> objects representing the streaming response.</returns>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        AutoSelectingChatClientCandidate candidate = SelectCandidate(messages, options);
        ChatOptions normalizedOptions = CreateNormalizedOptions(candidate, options);
        return candidate.Client.GetStreamingResponseAsync(messages, normalizedOptions, cancellationToken);
    }

    /// <summary>
    /// Disposes the current instance and all candidate chat clients, ensuring that each client is disposed only once.
    /// </summary>
    /// <param name="disposing">A boolean value indicating whether the method is called from a Dispose method (true) or from a finalizer (false).</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HashSet<IChatClient> disposedClients = [InnerClient];
            foreach (AutoSelectingChatClientCandidate candidate in _candidates)
            {
                if (disposedClients.Add(candidate.Client))
                {
                    candidate.Client.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Selects a candidate chat client using the selector function and validates that the selected candidate is one of the provided candidates.
    /// </summary>
    /// <param name="messages">The chat messages to send.</param>
    /// <param name="options">The chat options to use.</param>
    /// <returns>The selected chat client candidate.</returns>
    private AutoSelectingChatClientCandidate SelectCandidate(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        AutoSelectingChatClientCandidate candidate = _selector(messages, options, _candidates);
        if (candidate is null || Array.IndexOf(_candidates, candidate) < 0)
        {
            Throw.InvalidOperationException(
                $"The {nameof(AutoSelectingChatClient)} selector must return one of the provided candidates.");
        }

        return candidate;
    }
}
