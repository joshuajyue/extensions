// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A simple multi-model chat router used as a step-1 implementation for auto-selection experiments.
/// </summary>
/// <remarks>
/// This type intentionally keeps routing logic minimal: it selects a candidate and forwards the call.
/// More advanced policy, telemetry, and fallback behavior can be layered later.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIAutoSelectingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class SelectingChatClient : DelegatingChatClient
{
    // Keys used to annotate the selected candidate in ChatOptions.AdditionalProperties
    internal const string SelectedCandidateNameKey = "auto_select.candidate_name";
    internal const string SelectedProviderNameKey = "auto_select.provider_name";
    internal const string SelectedModelIdKey = "auto_select.model_id";

    /// <summary>
    /// Specifies how model selection results are reused across requests.
    /// </summary>
    [Experimental(DiagnosticIds.Experiments.AIAutoSelectingChat, UrlFormat = DiagnosticIds.UrlFormat)]
    public enum SelectionStickiness
    {
        /// <summary>Run selection logic for every request.</summary>
        EveryCall = 0,

        /// <summary>Select once per <see cref="SelectingChatClient"/> instance and reuse for all requests.</summary>
        PerInstance = 1,

        /// <summary>
        /// Select once per <see cref="ChatOptions.ConversationId"/> and reuse for subsequent requests in that conversation.
        /// If <see cref="ChatOptions.ConversationId"/> is missing, selection falls back to <see cref="EveryCall"/>.
        /// </summary>
        ByConversationId = 2,
    }

    // List of candidates
    private readonly SelectingChatClientCandidate[] _candidates;

    // Selector function either provided by the user, or defaulting to a built-in selector.
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<SelectingChatClientCandidate>, SelectingChatClientCandidate> _selector;

    // Sticky selection behavior.
    private readonly SelectionStickiness _stickiness;

    private readonly object _syncLock = new();
    private readonly Dictionary<string, SelectingChatClientCandidate>? _candidateByConversationId;
    private SelectingChatClientCandidate? _instanceCandidate;

    /// <summary>
    /// Validates the provided candidates and returns an array of non-null candidates.
    /// </summary>
    /// <param name="candidates">The list of candidates to validate.</param>
    /// <returns>An array of validated candidates.</returns>
    private static SelectingChatClientCandidate[] ValidateCandidates(IReadOnlyList<SelectingChatClientCandidate> candidates)
    {
        _ = Throw.IfNull(candidates);

        if (candidates.Count == 0)
        {
            Throw.ArgumentException(nameof(candidates), "At least one candidate client must be provided.");
        }

        var result = new SelectingChatClientCandidate[candidates.Count];
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
    private static ChatOptions CreateNormalizedOptions(SelectingChatClientCandidate candidate, ChatOptions? options)
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
    /// Initializes a new instance of the <see cref="SelectingChatClient"/> class with the specified candidates and an optional selector function.
    /// </summary>
    /// <param name="candidates">The list of candidate chat clients.</param>
    /// <param name="selector">An optional selector function to choose a candidate.</param>
    public SelectingChatClient(
        IReadOnlyList<SelectingChatClientCandidate> candidates,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<SelectingChatClientCandidate>, SelectingChatClientCandidate>? selector = null)
        : this(candidates, SelectionStickiness.EveryCall, selector)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectingChatClient"/> class with the specified candidates, stickiness behavior,
    /// and an optional selector function.
    /// </summary>
    /// <param name="candidates">The list of candidate chat clients.</param>
    /// <param name="stickiness">How selected candidates should be reused across calls.</param>
    /// <param name="selector">An optional selector function to choose a candidate.</param>
    public SelectingChatClient(
        IReadOnlyList<SelectingChatClientCandidate> candidates,
        SelectionStickiness stickiness,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<SelectingChatClientCandidate>, SelectingChatClientCandidate>? selector = null)
        : this(ValidateCandidates(candidates), stickiness, selector)
    {
    }

    private SelectingChatClient(
        SelectingChatClientCandidate[] candidates,
        SelectionStickiness stickiness,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyList<SelectingChatClientCandidate>, SelectingChatClientCandidate>? selector)
        : base(candidates[0].Client)
    {
        if (!Enum.IsDefined(typeof(SelectionStickiness), stickiness))
        {
            Throw.ArgumentOutOfRangeException(nameof(stickiness));
        }

        _candidates = candidates;
        _stickiness = stickiness;
        _selector = selector ?? SelectCandidateByBusinessLogic;

        if (stickiness == SelectionStickiness.ByConversationId)
        {
            _candidateByConversationId = new(StringComparer.Ordinal);
        }
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

        SelectingChatClientCandidate candidate = SelectCandidate(messages, options);
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

        SelectingChatClientCandidate candidate = SelectCandidate(messages, options);
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
            foreach (SelectingChatClientCandidate candidate in _candidates)
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
    private SelectingChatClientCandidate SelectCandidate(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        switch (_stickiness)
        {
            case SelectionStickiness.EveryCall:
                return ValidateSelectedCandidate(RunSelection(messages, options));

            case SelectionStickiness.PerInstance:
                lock (_syncLock)
                {
                    _instanceCandidate ??= ValidateSelectedCandidate(RunSelection(messages, options));
                    return _instanceCandidate;
                }

            case SelectionStickiness.ByConversationId:
                string? conversationId = options?.ConversationId;
                if (string.IsNullOrWhiteSpace(conversationId))
                {
                    // If ConversationId is missing, fall back to per-call selection.
                    return ValidateSelectedCandidate(RunSelection(messages, options));
                }

                lock (_syncLock)
                {
                    string nonWhitespaceConversationId = conversationId!;
                    if (_candidateByConversationId!.TryGetValue(nonWhitespaceConversationId, out SelectingChatClientCandidate? stickyCandidate))
                    {
                        return stickyCandidate;
                    }

                    SelectingChatClientCandidate selected = ValidateSelectedCandidate(RunSelection(messages, options));
                    _candidateByConversationId[nonWhitespaceConversationId] = selected;
                    return selected;
                }

            default:
                Throw.InvalidOperationException($"Unsupported {nameof(SelectionStickiness)} value: {_stickiness}.");
                return default!;
        }
    }

    private SelectingChatClientCandidate RunSelection(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        _selector(messages, options, _candidates);

    // Placeholder for future business logic: currently picks the first candidate.
    private SelectingChatClientCandidate SelectCandidateByBusinessLogic(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<SelectingChatClientCandidate> candidates)
    {
        _ = messages;
        _ = options;
        return candidates[0];
    }

    private SelectingChatClientCandidate ValidateSelectedCandidate(SelectingChatClientCandidate candidate)
    {
        if (candidate is null || Array.IndexOf(_candidates, candidate) < 0)
        {
            Throw.InvalidOperationException(
                $"The {nameof(SelectingChatClient)} selector must return one of the provided candidates.");
        }

        return candidate;
    }
}
