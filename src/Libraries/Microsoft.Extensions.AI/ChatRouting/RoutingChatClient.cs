// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
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
/// caches decisions according to a <see cref="RoutingStickiness"/> scope, walks fallbacks on failure,
/// and stamps the chosen model onto the response. It holds no opinion about which model is better —
/// that is entirely delegated to an <see cref="IChatRouteSelector"/> (the <em>policy</em>). When no
/// selector is supplied, the default is deterministic and opinion-free: it honors
/// <see cref="ChatOptions.ModelId"/> when set, otherwise it uses the first registered model.
/// </para>
/// <para>
/// A selector returns a <see cref="ChatRoutePlan"/> of the models it prefers, primary first. The router
/// attempts those in order; if every model in the plan fails, an optional <em>fallback policy</em> (a
/// delegate supplied via the constructor or <see cref="RoutingChatClientBuilder.UseFallback()"/>) decides
/// the order in which any remaining registered models are tried. This lets a selector that naturally picks
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

    private readonly RoutingChatModel[] _models;
    private readonly ReadOnlyCollection<RoutingChatModel> _modelList;
    private readonly IChatRouteSelector? _selector;
    private readonly RoutingStickiness _stickiness;
    private readonly Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>>? _fallback;

    private readonly ConcurrentDictionary<string, ChatRoutePlan>? _planByConversationId;
    private ChatRoutePlan? _instancePlan;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClient"/> class.</summary>
    /// <param name="models">The models to route between. At least one is required, each bound to an <see cref="IChatClient"/>.</param>
    /// <param name="selector">The selection policy, or <see langword="null"/> for the opinion-free default.</param>
    /// <param name="stickiness">How a routing decision is cached and reused across requests.</param>
    /// <param name="fallback">
    /// An optional fallback policy. After every model in the selected <see cref="ChatRoutePlan"/> has failed,
    /// this delegate receives the route context and the registered models not already in the plan, and returns
    /// the order in which to try them. When <see langword="null"/>, the router only attempts the plan's models.
    /// </param>
    public RoutingChatClient(
        IReadOnlyList<RoutingChatModel> models,
        IChatRouteSelector? selector = null,
        RoutingStickiness stickiness = RoutingStickiness.EveryCall,
        Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>>? fallback = null)
    {
        if (!Enum.IsDefined(typeof(RoutingStickiness), stickiness))
        {
            Throw.ArgumentOutOfRangeException(nameof(stickiness));
        }

        _models = ValidateModels(models);
        _modelList = new ReadOnlyCollection<RoutingChatModel>(_models);
        _selector = selector;
        _stickiness = stickiness;
        _fallback = fallback;

        if (stickiness == RoutingStickiness.ByConversationId)
        {
            _planByConversationId = new(StringComparer.Ordinal);
        }
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);

        IEnumerable<ChatMessage> normalizedMessages = NormalizeMessages(messages);
        var context = new ChatRouteContext(normalizedMessages, options, _modelList);
        ChatRoutePlan plan = await GetPlanAsync(context, cancellationToken);

        RoutingChatModel[] ordered = BuildAttemptOrder(plan, context);
        for (int i = 0; i < ordered.Length; i++)
        {
            RoutingChatModel model = ValidateRoutedModel(ordered[i]);
            ChatOptions? forwarded = CreateForwardedOptions(model, options);

            try
            {
                ChatResponse response = await model.Client!.GetResponseAsync(normalizedMessages, forwarded, cancellationToken);
                StampResponse(response, model);
                return response;
            }
            catch (Exception) when (i < ordered.Length - 1 && !cancellationToken.IsCancellationRequested)
            {
                // Fall back to the next model in the attempt order.
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
        var context = new ChatRouteContext(normalizedMessages, options, _modelList);
        ChatRoutePlan plan = await GetPlanAsync(context, cancellationToken);

        RoutingChatModel[] ordered = BuildAttemptOrder(plan, context);
        for (int i = 0; i < ordered.Length; i++)
        {
            RoutingChatModel model = ValidateRoutedModel(ordered[i]);
            ChatOptions? forwarded = CreateForwardedOptions(model, options);

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                model.Client!.GetStreamingResponseAsync(normalizedMessages, forwarded, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                // Fallback applies only until the first update is produced.
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception) when (i < ordered.Length - 1 && !cancellationToken.IsCancellationRequested)
            {
                await enumerator.DisposeAsync();
                continue;
            }
            catch
            {
                await enumerator.DisposeAsync();
                throw;
            }

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

    private async ValueTask<ChatRoutePlan> GetPlanAsync(ChatRouteContext context, CancellationToken cancellationToken)
    {
        switch (_stickiness)
        {
            case RoutingStickiness.PerInstance:
            {
                ChatRoutePlan? cached = Volatile.Read(ref _instancePlan);
                if (cached is not null && await PlanRemainsValidAsync(cached, context, cancellationToken))
                {
                    return cached;
                }

                ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
                Volatile.Write(ref _instancePlan, plan);
                return plan;
            }

            case RoutingStickiness.ByConversationId:
            {
                string? conversationId = context.Options?.ConversationId;
                if (string.IsNullOrWhiteSpace(conversationId))
                {
                    // Without a conversation id there is nothing to key on; behave like EveryCall.
                    return await RunSelectorAsync(context, cancellationToken);
                }

                if (_planByConversationId!.TryGetValue(conversationId!, out ChatRoutePlan? cached) &&
                    await PlanRemainsValidAsync(cached, context, cancellationToken))
                {
                    return cached;
                }

                ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
                _planByConversationId[conversationId!] = plan;
                return plan;
            }

            default:
                return await RunSelectorAsync(context, cancellationToken);
        }
    }

    private ValueTask<ChatRoutePlan> RunSelectorAsync(ChatRouteContext context, CancellationToken cancellationToken) =>
        _selector is null
            ? new ValueTask<ChatRoutePlan>(DefaultSelectRoute(context))
            : _selector.SelectRouteAsync(context, cancellationToken);

    // Builds the ordered sequence the router actually attempts: the selected plan's models first, then —
    // if a fallback policy is configured — the registered models the plan omitted, in the order the policy
    // returns. Duplicates and models already in the plan are dropped so each candidate is tried at most once.
    private RoutingChatModel[] BuildAttemptOrder(ChatRoutePlan plan, ChatRouteContext context)
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
        var result = new List<RoutingChatModel>(_models.Length);
        foreach (RoutingChatModel model in primary)
        {
            if (seen.Add(model))
            {
                result.Add(model);
            }
        }

        var remaining = new List<RoutingChatModel>(_models.Length);
        foreach (RoutingChatModel model in _models)
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

    private static async ValueTask<bool> PlanRemainsValidAsync(ChatRoutePlan plan, ChatRouteContext context, CancellationToken cancellationToken) =>
        plan.RemainsValid is null || await plan.RemainsValid(context, cancellationToken);

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
        // Only the provider model id is forwarded, and only when the caller did not pin one.
        // Routing internals are never written into the forwarded request.
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
