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

/// <summary>Builds a <see cref="RoutingChatClient"/> from catalog and custom model registrations.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class RoutingChatClientBuilder
{
    private readonly List<RoutingChatModel> _models = [];
    private IChatRouteSelector? _selector;
    private RoutingStickiness _stickiness = RoutingStickiness.EveryCall;
    private Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>>? _fallback;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClientBuilder"/> class.</summary>
    /// <param name="catalog">Optional catalog used by <see cref="AddFromCatalog(string, IChatClient)"/>.</param>
    public RoutingChatClientBuilder(RoutingChatModelCatalog? catalog = null)
    {
        Catalog = catalog;
    }

    /// <summary>Gets the optional catalog used by <see cref="AddFromCatalog(string, IChatClient)"/>.</summary>
    public RoutingChatModelCatalog? Catalog { get; }

    /// <summary>Adds a model from this builder's catalog.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="client">The chat client to use when the model is selected.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder AddFromCatalog(string name, IChatClient client)
    {
        if (Catalog is null)
        {
            Throw.InvalidOperationException(
                $"A {nameof(RoutingChatModelCatalog)} must be supplied to use {nameof(AddFromCatalog)}.");
        }

        return AddModel(Catalog!.Get(name), client);
    }

    /// <summary>Adds a model from the specified catalog.</summary>
    /// <param name="catalog">The catalog containing the entry.</param>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="client">The chat client to use when the model is selected.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder AddFromCatalog(RoutingChatModelCatalog catalog, string name, IChatClient client) =>
        AddModel(Throw.IfNull(catalog).Get(name), client);

    /// <summary>Adds a custom model.</summary>
    /// <param name="name">A stable name identifying this model.</param>
    /// <param name="client">The chat client to use when the model is selected.</param>
    /// <param name="providerName">Optional provider name.</param>
    /// <param name="modelId">Optional provider-specific model identifier.</param>
    /// <param name="traits">Optional high-level traits for the model.</param>
    /// <param name="maxInputTokens">Optional maximum number of input (context window) tokens the model accepts.</param>
    /// <param name="inputTokenCostPerMillion">Optional input token cost per million tokens.</param>
    /// <param name="outputTokenCostPerMillion">Optional output token cost per million tokens.</param>
    /// <param name="typicalLatency">Optional representative latency hint.</param>
    /// <param name="sourceUri">Optional source used for the model metadata.</param>
    /// <param name="updatedAt">Optional time the model metadata was last updated.</param>
    /// <param name="additionalProperties">Optional additional metadata associated with the model.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder AddModel(
        string name,
        IChatClient client,
        string? providerName = null,
        string? modelId = null,
        RoutingChatModelTraits traits = RoutingChatModelTraits.None,
        int? maxInputTokens = null,
        decimal? inputTokenCostPerMillion = null,
        decimal? outputTokenCostPerMillion = null,
        TimeSpan? typicalLatency = null,
        Uri? sourceUri = null,
        DateTimeOffset? updatedAt = null,
        AdditionalPropertiesDictionary? additionalProperties = null) =>
        AddModel(
            new RoutingChatModel(
                name,
                providerName,
                modelId,
                traits,
                maxInputTokens,
                inputTokenCostPerMillion,
                outputTokenCostPerMillion,
                typicalLatency,
                sourceUri,
                updatedAt,
                additionalProperties,
                Throw.IfNull(client)));

    /// <summary>Adds a model using metadata from an existing catalog entry.</summary>
    /// <param name="entry">The model metadata.</param>
    /// <param name="client">The chat client to use when the model is selected.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder AddModel(RoutingChatModel entry, IChatClient client) =>
        AddModel(Throw.IfNull(entry).WithClient(client));

    /// <summary>Adds a model that is already bound to a chat client.</summary>
    /// <param name="entry">The model.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder AddModel(RoutingChatModel entry)
    {
        _ = Throw.IfNull(entry);
        if (entry.Client is null)
        {
            Throw.ArgumentException(nameof(entry), $"The model '{entry.Name}' must be bound to an IChatClient.");
        }

        foreach (RoutingChatModel existing in _models)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(existing.Name, entry.Name))
            {
                Throw.ArgumentException(nameof(entry), $"A model named '{entry.Name}' has already been added.");
            }
        }

        _models.Add(entry);
        return this;
    }

    /// <summary>Sets the selection policy used to route requests.</summary>
    /// <param name="selector">The selector, or <see langword="null"/> to use the opinion-free default.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder UseSelector(IChatRouteSelector? selector)
    {
        _selector = selector;
        return this;
    }

    /// <summary>Sets the selection policy from an asynchronous delegate.</summary>
    /// <param name="selector">The delegate that produces a plan for a given context.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder UseSelector(Func<ChatRouteContext, CancellationToken, ValueTask<ChatRoutePlan>> selector) =>
        UseSelector(ChatRouteSelector.Create(selector));

    /// <summary>Sets the selection policy from a synchronous delegate.</summary>
    /// <param name="selector">The delegate that produces a plan for a given context.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder UseSelector(Func<ChatRouteContext, ChatRoutePlan> selector) =>
        UseSelector(ChatRouteSelector.Create(selector));

    /// <summary>Sets how a routing decision is cached and reused across requests.</summary>
    /// <param name="stickiness">The stickiness scope.</param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder UseStickiness(RoutingStickiness stickiness)
    {
        _stickiness = stickiness;
        return this;
    }

    /// <summary>Enables fallback over the registered models a selector's plan omitted, in registration order.</summary>
    /// <returns>The current builder.</returns>
    /// <remarks>
    /// After every model in the selected <see cref="ChatRoutePlan"/> has failed, the router tries the remaining
    /// registered models in the order they were added. This gives resilience to selectors that deliberately pick
    /// a single model (such as a complexity classifier) without the selector having to fabricate a ranking of the
    /// other models. For a different fallback order, use <see cref="UseFallback(Func{ChatRouteContext, IReadOnlyList{RoutingChatModel}, IReadOnlyList{RoutingChatModel}})"/>.
    /// </remarks>
    public RoutingChatClientBuilder UseFallback() =>
        UseFallback(static (_, remaining) => remaining);

    /// <summary>Sets a custom fallback policy used after a selector's plan is exhausted.</summary>
    /// <param name="fallback">
    /// A delegate that receives the route context and the registered models not already in the plan, and returns
    /// the order in which to try them. Returning an empty list disables fallback for that request.
    /// </param>
    /// <returns>The current builder.</returns>
    public RoutingChatClientBuilder UseFallback(
        Func<ChatRouteContext, IReadOnlyList<RoutingChatModel>, IReadOnlyList<RoutingChatModel>> fallback)
    {
        _fallback = Throw.IfNull(fallback);
        return this;
    }

    /// <summary>Builds a <see cref="RoutingChatClient"/>.</summary>
    /// <returns>A routing chat client.</returns>
    public RoutingChatClient Build() => new(_models, _selector, _stickiness, _fallback);
}
