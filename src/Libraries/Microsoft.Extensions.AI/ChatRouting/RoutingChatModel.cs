// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Describes a model that a <see cref="RoutingChatClient"/> can route to.</summary>
/// <remarks>
/// A model carries optional metadata (provider, model id, traits, context window, cost, latency) and
/// is bound to an <see cref="IChatClient"/> when used at runtime. Metadata-only instances (with no
/// <see cref="Client"/>) can be stored in a <see cref="RoutingChatModelCatalog"/> and bound to a
/// concrete client later. The metadata is advisory: the routing mechanism never interprets it, and
/// only a selection policy (an <see cref="IChatRouteSelector"/>) decides how to use it.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class RoutingChatModel
{
    /// <summary>Initializes a new instance of the <see cref="RoutingChatModel"/> class.</summary>
    /// <param name="name">A stable name identifying this model.</param>
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
    /// <param name="client">Optional chat client to use when this model is selected.</param>
    public RoutingChatModel(
        string name,
        string? providerName = null,
        string? modelId = null,
        RoutingChatModelTraits traits = RoutingChatModelTraits.None,
        int? maxInputTokens = null,
        decimal? inputTokenCostPerMillion = null,
        decimal? outputTokenCostPerMillion = null,
        TimeSpan? typicalLatency = null,
        Uri? sourceUri = null,
        DateTimeOffset? updatedAt = null,
        AdditionalPropertiesDictionary? additionalProperties = null,
        IChatClient? client = null)
    {
        if (maxInputTokens < 0)
        {
            Throw.ArgumentOutOfRangeException(nameof(maxInputTokens));
        }

        if (inputTokenCostPerMillion < 0)
        {
            Throw.ArgumentOutOfRangeException(nameof(inputTokenCostPerMillion));
        }

        if (outputTokenCostPerMillion < 0)
        {
            Throw.ArgumentOutOfRangeException(nameof(outputTokenCostPerMillion));
        }

        if (typicalLatency < TimeSpan.Zero)
        {
            Throw.ArgumentOutOfRangeException(nameof(typicalLatency));
        }

        Name = Throw.IfNullOrWhitespace(name);
        ProviderName = providerName;
        ModelId = modelId;
        Traits = traits;
        MaxInputTokens = maxInputTokens;
        InputTokenCostPerMillion = inputTokenCostPerMillion;
        OutputTokenCostPerMillion = outputTokenCostPerMillion;
        TypicalLatency = typicalLatency;
        SourceUri = sourceUri;
        UpdatedAt = updatedAt;
        AdditionalProperties = additionalProperties?.Clone();
        Client = client;
    }

    /// <summary>Gets the stable name identifying this model.</summary>
    public string Name { get; }

    /// <summary>Gets the optional provider name.</summary>
    public string? ProviderName { get; }

    /// <summary>Gets the optional provider-specific model identifier.</summary>
    public string? ModelId { get; }

    /// <summary>Gets the optional high-level model traits.</summary>
    public RoutingChatModelTraits Traits { get; }

    /// <summary>Gets the optional maximum number of input (context window) tokens the model accepts.</summary>
    public int? MaxInputTokens { get; }

    /// <summary>Gets the optional input token cost per million tokens.</summary>
    public decimal? InputTokenCostPerMillion { get; }

    /// <summary>Gets the optional output token cost per million tokens.</summary>
    public decimal? OutputTokenCostPerMillion { get; }

    /// <summary>Gets the optional representative latency hint.</summary>
    public TimeSpan? TypicalLatency { get; }

    /// <summary>Gets the optional source used for this model metadata.</summary>
    public Uri? SourceUri { get; }

    /// <summary>Gets the optional time the model metadata was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>Gets any additional metadata associated with the model.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; }

    /// <summary>Gets the optional <see cref="IChatClient"/> to use when this model is selected.</summary>
    public IChatClient? Client { get; }

    /// <summary>Creates a copy of this model bound to the specified <paramref name="client"/>.</summary>
    /// <param name="client">The chat client to use when this model is selected.</param>
    /// <returns>A model with the same metadata as this instance and the specified client.</returns>
    public RoutingChatModel WithClient(IChatClient client) =>
        new(
            Name,
            ProviderName,
            ModelId,
            Traits,
            MaxInputTokens,
            InputTokenCostPerMillion,
            OutputTokenCostPerMillion,
            TypicalLatency,
            SourceUri,
            UpdatedAt,
            AdditionalProperties,
            Throw.IfNull(client));
}
