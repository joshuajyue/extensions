// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Describes a route that a <c>RoutingChatClient</c> can dispatch to.</summary>
/// <remarks>
/// A route carries optional metadata (provider, model id, context window, cost, latency, and open
/// capability tokens) and is bound to an <see cref="IChatClient"/> when used at runtime. Because the client
/// may itself be another <c>RoutingChatClient</c>, a route is a node in a routing tree, not necessarily a
/// concrete provider model — which is why it is a <em>route</em> rather than a "model". Metadata-only
/// instances (with no <see cref="Client"/>) can be stored in a <see cref="ChatRouteCatalog"/> and bound to a
/// concrete client later. The metadata is advisory: the routing mechanism never interprets it, and only a
/// selection policy (an <see cref="IChatRouteSelector"/>) decides how to use it. In particular, no built-in
/// selector reads the cost, context-window, or latency hints — cost- or context-aware routing is
/// bring-your-own-selector: supply an <see cref="IChatRouteSelector"/> that reads these properties (and any
/// <see cref="AdditionalProperties"/>). Capability tokens declared under
/// <see cref="ChatModelCapabilities.PropertyKey"/> in <see cref="AdditionalProperties"/> are the exception the
/// mechanism does read, via the router's capability detector.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class ChatRoute
{
    /// <summary>Initializes a new instance of the <see cref="ChatRoute"/> class.</summary>
    /// <param name="name">A stable name identifying this route.</param>
    /// <param name="providerName">Optional provider name.</param>
    /// <param name="modelId">Optional provider-specific model identifier.</param>
    /// <param name="maxInputTokens">Optional maximum number of input (context window) tokens the route accepts.</param>
    /// <param name="inputTokenCostPerMillion">Optional input token cost per million tokens.</param>
    /// <param name="outputTokenCostPerMillion">Optional output token cost per million tokens.</param>
    /// <param name="typicalLatency">Optional representative latency hint.</param>
    /// <param name="sourceUri">Optional source used for the route metadata.</param>
    /// <param name="updatedAt">Optional time the route metadata was last updated.</param>
    /// <param name="additionalProperties">Optional additional metadata associated with the route (including capability tokens under <see cref="ChatModelCapabilities.PropertyKey"/>).</param>
    /// <param name="client">Optional chat client to use when this route is selected.</param>
    public ChatRoute(
        string name,
        string? providerName = null,
        string? modelId = null,
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
        MaxInputTokens = maxInputTokens;
        InputTokenCostPerMillion = inputTokenCostPerMillion;
        OutputTokenCostPerMillion = outputTokenCostPerMillion;
        TypicalLatency = typicalLatency;
        SourceUri = sourceUri;
        UpdatedAt = updatedAt;
        AdditionalProperties = additionalProperties?.Clone();
        Client = client;
    }

    /// <summary>Gets the stable name identifying this route.</summary>
    public string Name { get; }

    /// <summary>Gets the optional provider name.</summary>
    public string? ProviderName { get; }

    /// <summary>Gets the optional provider-specific model identifier.</summary>
    public string? ModelId { get; }

    /// <summary>Gets the optional maximum number of input (context window) tokens the route accepts.</summary>
    public int? MaxInputTokens { get; }

    /// <summary>Gets the optional input token cost per million tokens.</summary>
    public decimal? InputTokenCostPerMillion { get; }

    /// <summary>Gets the optional output token cost per million tokens.</summary>
    public decimal? OutputTokenCostPerMillion { get; }

    /// <summary>Gets the optional representative latency hint.</summary>
    public TimeSpan? TypicalLatency { get; }

    /// <summary>Gets the optional source used for this route metadata.</summary>
    public Uri? SourceUri { get; }

    /// <summary>Gets the optional time the route metadata was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>Gets any additional metadata associated with the route.</summary>
    public AdditionalPropertiesDictionary? AdditionalProperties { get; }

    /// <summary>Gets the optional <see cref="IChatClient"/> to use when this route is selected.</summary>
    public IChatClient? Client { get; }

    /// <summary>Creates a copy of this route bound to the specified <paramref name="client"/>.</summary>
    /// <param name="client">The chat client to use when this route is selected.</param>
    /// <returns>A route with the same metadata as this instance and the specified client.</returns>
    public ChatRoute WithClient(IChatClient client) =>
        new(
            Name,
            ProviderName,
            ModelId,
            MaxInputTokens,
            InputTokenCostPerMillion,
            OutputTokenCostPerMillion,
            TypicalLatency,
            SourceUri,
            UpdatedAt,
            AdditionalProperties,
            Throw.IfNull(client));
}
