// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

#pragma warning disable SA1204 // Static members should appear before non-static members

namespace Microsoft.Extensions.AI;

/// <summary>
/// An <see cref="IChatClient"/> that routes each request to one of several inner models chosen by a
/// swappable selection policy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutingChatClient"/> is the routing <em>mechanism</em>: it owns the candidate models,
/// runs the selector once per request, walks fallbacks on failure, and stamps the chosen model onto the
/// response. It holds no opinion about which model is better —
/// that is entirely delegated to an <see cref="IChatRouteSelector"/> (the <em>policy</em>). When no
/// selector is supplied, the default is deterministic and opinion-free: it honors
/// <see cref="ChatOptions.ModelId"/> when set, otherwise it uses the first registered model.
/// </para>
/// <para>
/// Before a selector runs, the router applies a soft <em>capability gate</em>: it narrows the candidate
/// models to those that can satisfy capabilities the request provably needs (image content requires
/// <see cref="RoutingChatModelTraits.Vision"/>; supplied <see cref="ChatOptions.Tools"/> require
/// <see cref="RoutingChatModelTraits.ToolCalling"/>). This is a correctness filter shared by every selector
/// and the fallback chain — not a quality signal. It is soft: when no registered model declares a required
/// capability, the gate falls through to the full set rather than stranding the request, and it can be
/// bypassed entirely via <see cref="RoutingChatClientBuilder.UseCapabilityGate(bool)"/>.
/// </para>
/// <para>
/// A selector returns a <see cref="ChatRoutePlan"/> of the models it prefers, primary first. The router
/// attempts those in order; if every model in the plan fails, an optional <em>fallback policy</em> (a
/// delegate supplied via the constructor or <see cref="RoutingChatClientBuilder.UseFallback()"/>) decides
/// the order in which any remaining candidate models are tried. This lets a selector that naturally picks
/// a single model (for example a complexity classifier) stay out of the fallback business: it returns just
/// its primary, and the router owns failure handling.
/// </para>
/// <para>
/// Because each candidate is itself an <see cref="IChatClient"/>, a routing pipeline forms a tree:
/// a candidate may have its own middleware, or may itself be another <see cref="RoutingChatClient"/>.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class RoutingChatClient : IChatClient
{
    /// <summary>The key under which the selected model's name is stamped onto a response.</summary>
    public const string SelectedModelNameKey = "routing.selected_model";

    /// <summary>The key under which the selected model's identifier is stamped onto a response.</summary>
    public const string SelectedModelIdKey = "routing.selected_model_id";

    /// <summary>The key under which the selected model's provider name is stamped onto a response.</summary>
    public const string SelectedProviderNameKey = "routing.selected_provider";

    /// <summary>
    /// The name of the <see cref="ActivityEvent"/> the router adds to <see cref="Activity.Current"/> for each
    /// model it attempts. Read these events with an <see cref="ActivityListener"/> (or any OpenTelemetry trace
    /// exporter) to observe the full per-request attempt timeline — the order models were tried, which failed
    /// and why, and how long each took.
    /// </summary>
    public const string AttemptEventName = "routing.attempt";

    /// <summary>
    /// The name of the <see cref="ActivityEvent"/> the router adds to <see cref="Activity.Current"/> once per
    /// request describing the routing decision: the selected model and any decision-rationale a selector attached
    /// via <see cref="ChatRoutePlan.DecisionMetadata"/> (for example a complexity tier or a semantic similarity
    /// score). Read it with an <see cref="ActivityListener"/> or any OpenTelemetry trace exporter.
    /// </summary>
    public const string DecisionEventName = "routing.decision";

    // Tag keys carried by each routing.attempt event. Kept private: the event/tag schema is a telemetry detail
    // observed through ActivityListener, not a programmatic API surface.
    private const string AttemptOrdinalKey = "routing.attempt.ordinal";
    private const string AttemptModelKey = "routing.attempt.model";
    private const string AttemptModelIdKey = "routing.attempt.model_id";
    private const string AttemptProviderKey = "routing.attempt.provider";
    private const string AttemptOutcomeKey = "routing.attempt.outcome";
    private const string AttemptDurationMsKey = "routing.attempt.duration_ms";
    private const string AttemptErrorTypeKey = "routing.attempt.error_type";

    // routing.attempt.outcome values.
    private const string AttemptOutcomeSuccess = "success";   // this model produced a response (or first token)
    private const string AttemptOutcomeFallback = "fallback"; // this model failed; the router fell back to the next
    private const string AttemptOutcomeError = "error";       // this model failed and no fallback remained (propagates)

    private readonly RoutingChatModel[] _models;
    private readonly ReadOnlyCollection<RoutingChatModel> _modelList;
    private readonly IChatRouteSelector? _selector;
    private readonly Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>>? _fallback;
    private readonly bool _capabilityGate;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClient"/> class.</summary>
    /// <param name="models">The models to route between. At least one is required, each bound to an <see cref="IChatClient"/>.</param>
    /// <param name="selector">The selection policy, or <see langword="null"/> for the opinion-free default.</param>
    /// <param name="fallback">
    /// An optional fallback policy. After every model in the selected <see cref="ChatRoutePlan"/> has failed,
    /// this delegate receives the route context and the registered models not already in the plan, and returns
    /// the order in which to try them. When <see langword="null"/>, the router only attempts the plan's models.
    /// </param>
    /// <param name="capabilityGate">
    /// When <see langword="true"/> (the default), the router narrows the candidate models to those that can satisfy
    /// capabilities the request provably needs before the selector runs — a model with image content requires
    /// <see cref="RoutingChatModelTraits.Vision"/>, and supplying tools requires <see cref="RoutingChatModelTraits.ToolCalling"/>.
    /// The gate is soft: if no registered model declares a required capability, it falls through to the full set rather
    /// than stranding the request. When <see langword="false"/>, every registered model is always a candidate.
    /// </param>
    public RoutingChatClient(
        IReadOnlyList<RoutingChatModel> models,
        IChatRouteSelector? selector = null,
        Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>>? fallback = null,
        bool capabilityGate = true)
    {
        _models = ValidateModels(models);
        _modelList = new ReadOnlyCollection<RoutingChatModel>(_models);
        _selector = selector;
        _fallback = fallback;
        _capabilityGate = capabilityGate;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        IEnumerable<ChatMessage> normalizedMessages = NormalizeMessages(messages);
        IReadOnlyList<RoutingChatModel> candidates = GetCandidateModels(normalizedMessages, options);
        var context = new ChatRouteContext(normalizedMessages, options, candidates);
        ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
        RecordDecisionEvent(plan);

        RoutingChatModel[] ordered = BuildAttemptOrder(plan, context, candidates);
        for (int i = 0; i < ordered.Length; i++)
        {
            RoutingChatModel model = ValidateRoutedModel(ordered[i]);
            ChatOptions? forwarded = CreateForwardedOptions(model, options);
            long startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                ChatResponse response = await model.Client!.GetResponseAsync(normalizedMessages, forwarded, cancellationToken);
                RecordAttemptEvent(i + 1, model, AttemptOutcomeSuccess, startTimestamp, error: null);
                StampResponse(response, model);
                return response;
            }
            catch (Exception ex) when (i < ordered.Length - 1 && !cancellationToken.IsCancellationRequested)
            {
                RecordAttemptEvent(i + 1, model, AttemptOutcomeFallback, startTimestamp, ex);

                // Fall back to the next model in the attempt order.
            }
            catch (Exception ex)
            {
                RecordAttemptEvent(i + 1, model, AttemptOutcomeError, startTimestamp, ex);
                throw;
            }
        }

        // Unreachable: the loop above always returns or rethrows on the final model.
        throw new InvalidOperationException("Routing produced no model to invoke.");
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        IEnumerable<ChatMessage> normalizedMessages = NormalizeMessages(messages);
        IReadOnlyList<RoutingChatModel> candidates = GetCandidateModels(normalizedMessages, options);
        var context = new ChatRouteContext(normalizedMessages, options, candidates);
        ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
        RecordDecisionEvent(plan);

        RoutingChatModel[] ordered = BuildAttemptOrder(plan, context, candidates);
        for (int i = 0; i < ordered.Length; i++)
        {
            RoutingChatModel model = ValidateRoutedModel(ordered[i]);
            ChatOptions? forwarded = CreateForwardedOptions(model, options);
            long startTimestamp = Stopwatch.GetTimestamp();

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                model.Client!.GetStreamingResponseAsync(normalizedMessages, forwarded, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                // Fallback applies only until the first update is produced.
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (i < ordered.Length - 1 && !cancellationToken.IsCancellationRequested)
            {
                RecordAttemptEvent(i + 1, model, AttemptOutcomeFallback, startTimestamp, ex);
                await enumerator.DisposeAsync();
                continue;
            }
            catch (Exception ex)
            {
                RecordAttemptEvent(i + 1, model, AttemptOutcomeError, startTimestamp, ex);
                await enumerator.DisposeAsync();
                throw;
            }

            // The first token committed this model: from the router's perspective the attempt succeeded, and
            // the recorded duration is its time-to-first-token. Mid-stream failures past here are not a routing
            // fallback decision, so they are not recorded as attempts.
            RecordAttemptEvent(i + 1, model, AttemptOutcomeSuccess, startTimestamp, error: null);
            StampActivity(model);
            try
            {
                bool stamped = false;
                while (hasFirst)
                {
                    ChatResponseUpdate update = enumerator.Current;
                    if (!stamped)
                    {
                        StampUpdate(update, model);
                        stamped = true;
                    }

                    yield return update;
                    hasFirst = await enumerator.MoveNextAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            yield break;
        }
    }

    /// <inheritdoc/>
    public virtual object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceKey is null && _selector is not null && serviceType.IsInstanceOfType(_selector) ? _selector : null;
    }

    /// <summary>Disposes the current instance and all model chat clients, ensuring that each client is disposed only once.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the current instance and all model chat clients, ensuring that each client is disposed only once.</summary>
    /// <param name="disposing"><see langword="true"/> if being called from <see cref="Dispose()"/>; otherwise, <see langword="false"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            HashSet<IChatClient> disposedClients = [];
            foreach (RoutingChatModel model in _models)
            {
                IChatClient client = model.Client!;
                if (disposedClients.Add(client))
                {
                    client.Dispose();
                }
            }
        }
    }

    private ValueTask<ChatRoutePlan> RunSelectorAsync(ChatRouteContext context, CancellationToken cancellationToken) =>
        _selector is null
            ? new ValueTask<ChatRoutePlan>(DefaultSelectRoute(context))
            : _selector.SelectRouteAsync(context, cancellationToken);

    // The router's capability gate. Narrows the registered models to those that can satisfy capabilities the
    // request provably needs, so every selector — and the fallback chain — only ever sees capable candidates.
    // Only high-confidence signals are used (image content -> Vision, supplied tools -> ToolCalling); nothing
    // is inferred from estimates. The gate is soft: when no registered model positively declares a required
    // capability (sparse or incorrect trait metadata), it returns the full set rather than stranding the request.
    private ReadOnlyCollection<RoutingChatModel> GetCandidateModels(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (!_capabilityGate)
        {
            return _modelList;
        }

        RoutingChatModelTraits required = GetRequiredCapabilities(messages, options);
        if (required == RoutingChatModelTraits.None)
        {
            return _modelList;
        }

        List<RoutingChatModel>? capable = null;
        foreach (RoutingChatModel model in _models)
        {
            if ((model.Traits & required) == required)
            {
                (capable ??= new List<RoutingChatModel>(_models.Length)).Add(model);
            }
        }

        return capable is { Count: > 0 } ? new ReadOnlyCollection<RoutingChatModel>(capable) : _modelList;
    }

    // Derives the capabilities a request provably requires, using only signals that cannot be wrong about the
    // request itself: a message carrying image content needs a vision model, and supplying tools needs a
    // tool-calling model. Fuzzier dimensions (such as "reasoning") are deliberately excluded — they are a
    // selector's job to weigh, not a hard correctness gate.
    private static RoutingChatModelTraits GetRequiredCapabilities(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        RoutingChatModelTraits required = RoutingChatModelTraits.None;

        if (options?.Tools is { Count: > 0 })
        {
            required |= RoutingChatModelTraits.ToolCalling;
        }

        if (MessagesContainImage(messages))
        {
            required |= RoutingChatModelTraits.Vision;
        }

        return required;
    }

    private static bool MessagesContainImage(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            IList<AIContent> contents = message.Contents;
            for (int i = 0; i < contents.Count; i++)
            {
                switch (contents[i])
                {
                    case DataContent data when data.HasTopLevelMediaType("image"):
                    case UriContent uri when uri.HasTopLevelMediaType("image"):
                        return true;
                }
            }
        }

        return false;
    }

    // Builds the ordered sequence the router actually attempts: the selected plan's models first, then —
    // if a fallback policy is configured — the candidate models the plan omitted, in the order the policy
    // returns. Duplicates and models already in the plan are dropped so each candidate is tried at most once.
    // The fallback tail is drawn from the same gated candidate set the selector saw, never the full registry.
    private RoutingChatModel[] BuildAttemptOrder(ChatRoutePlan plan, ChatRouteContext context, IReadOnlyList<RoutingChatModel> candidates)
    {
        IReadOnlyList<RoutingChatModel> primary = plan.OrderedModels;
        if (_fallback is null)
        {
            var planOnly = new RoutingChatModel[primary.Count];
            for (int i = 0; i < primary.Count; i++)
            {
                planOnly[i] = primary[i];
            }

            return planOnly;
        }

        var seen = new HashSet<RoutingChatModel>();
        var result = new List<RoutingChatModel>(candidates.Count);
        foreach (RoutingChatModel model in primary)
        {
            if (seen.Add(model))
            {
                result.Add(model);
            }
        }

        var remaining = new List<RoutingChatModel>(candidates.Count);
        foreach (RoutingChatModel model in candidates)
        {
            if (!seen.Contains(model))
            {
                remaining.Add(model);
            }
        }

        IReadOnlyList<RoutingChatModel>? tail = _fallback(context, remaining);
        if (tail is not null)
        {
            foreach (RoutingChatModel model in tail)
            {
                if (model is not null && seen.Add(model))
                {
                    result.Add(model);
                }
            }
        }

        return result.ToArray();
    }

    // The opinion-free default: honor an explicit ModelId, otherwise the first registered model.
    private static ChatRoutePlan DefaultSelectRoute(ChatRouteContext context)
    {
        string? modelId = context.Options?.ModelId;
        if (!string.IsNullOrEmpty(modelId))
        {
            foreach (RoutingChatModel model in context.Models)
            {
                if (string.Equals(model.ModelId, modelId, StringComparison.Ordinal) ||
                    string.Equals(model.Name, modelId, StringComparison.Ordinal))
                {
                    return new ChatRoutePlan(model);
                }
            }
        }

        return new ChatRoutePlan(context.Models[0]);
    }

    private static ChatOptions? CreateForwardedOptions(RoutingChatModel model, ChatOptions? options)
    {
        // The router forwards the caller's options, supplying the chosen provider model id only when the caller did
        // not pin one. It is applied on a clone, leaving the caller's options untouched.
        if (model.ModelId is null || options?.ModelId is not null)
        {
            return options;
        }

        ChatOptions forwarded = options?.Clone() ?? new ChatOptions();
        forwarded.ModelId = model.ModelId;
        return forwarded;
    }

    private static void StampResponse(ChatResponse response, RoutingChatModel model)
    {
        if (response is null)
        {
            return;
        }

        AdditionalPropertiesDictionary props = response.AdditionalProperties ??= [];
        props[SelectedModelNameKey] = model.Name;
        props[SelectedModelIdKey] = model.ModelId;
        props[SelectedProviderNameKey] = model.ProviderName;

        StampActivity(model);
    }

    private static void StampUpdate(ChatResponseUpdate update, RoutingChatModel model)
    {
        AdditionalPropertiesDictionary props = update.AdditionalProperties ??= [];
        props[SelectedModelNameKey] = model.Name;
        props[SelectedModelIdKey] = model.ModelId;
        props[SelectedProviderNameKey] = model.ProviderName;
    }

    private static void StampActivity(RoutingChatModel model)
    {
        Activity? activity = Activity.Current;
        if (activity is not null)
        {
            _ = activity.SetTag(SelectedModelNameKey, model.Name);
            if (model.ModelId is not null)
            {
                _ = activity.SetTag(SelectedModelIdKey, model.ModelId);
            }
        }
    }

    // Adds one routing.attempt event to the ambient span describing a single model attempt: its order, model
    // identity, outcome (success/fallback/error), elapsed time, and — on failure — the exception type. This is
    // the per-attempt timeline that StampActivity (winner-only) does not capture. It is a no-op unless a
    // listener is recording the current activity, so the cost is only paid when telemetry is being collected.
    private static void RecordAttemptEvent(int ordinal, RoutingChatModel model, string outcome, long startTimestamp, Exception? error)
    {
        Activity? activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            [AttemptOrdinalKey] = ordinal,
            [AttemptModelKey] = model.Name,
            [AttemptOutcomeKey] = outcome,
            [AttemptDurationMsKey] = FunctionInvocationHelpers.GetElapsedTime(startTimestamp).TotalMilliseconds,
        };

        if (model.ModelId is not null)
        {
            tags[AttemptModelIdKey] = model.ModelId;
        }

        if (model.ProviderName is not null)
        {
            tags[AttemptProviderKey] = model.ProviderName;
        }

        if (error is not null)
        {
            tags[AttemptErrorTypeKey] = error.GetType().FullName;
        }

        _ = activity.AddEvent(new ActivityEvent(AttemptEventName, tags: tags));
    }

    // Adds one routing.decision event to the ambient span describing the selected plan: the primary model and any
    // decision-rationale the selector attached (complexity tier, semantic score, ...). Fires once per request.
    // No-op unless a listener is recording the current activity.
    private static void RecordDecisionEvent(ChatRoutePlan plan)
    {
        Activity? activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            [SelectedModelNameKey] = plan.OrderedModels[0].Name,
        };

        if (plan.DecisionMetadata is { } metadata)
        {
            foreach (KeyValuePair<string, object> entry in metadata)
            {
                tags[entry.Key] = entry.Value;
            }
        }

        _ = activity.AddEvent(new ActivityEvent(DecisionEventName, tags: tags));
    }

    private static IEnumerable<ChatMessage> NormalizeMessages(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();

    private RoutingChatModel ValidateRoutedModel(RoutingChatModel model)
    {
        if (model is null || Array.IndexOf(_models, model) < 0)
        {
            Throw.InvalidOperationException(
                $"The {nameof(RoutingChatClient)} selector must route to one of the registered models.");
        }

        return model;
    }

    private static RoutingChatModel[] ValidateModels(IReadOnlyList<RoutingChatModel> models)
    {
        _ = Throw.IfNull(models);

        if (models.Count == 0)
        {
            Throw.ArgumentException(nameof(models), "At least one model must be provided.");
        }

        var result = new RoutingChatModel[models.Count];
        for (int i = 0; i < models.Count; i++)
        {
            result[i] = Throw.IfNull(models[i]);
            if (result[i].Client is null)
            {
                Throw.ArgumentException(nameof(models), $"The model '{result[i].Name}' must be bound to an IChatClient.");
            }
        }

        return result;
    }
}
