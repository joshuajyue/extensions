// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Options controlling how an external model catalog document is mapped to <see cref="RoutingChatModel"/> entries.</summary>
/// <remarks>
/// External catalogs (such as LiteLLM's <c>model_prices_and_context_window.json</c> or the GitHub
/// Models catalog at <c>https://models.github.ai/catalog/models</c>) are frequently updated sources
/// of objective per-model metadata: capability flags, context windows, and — where available —
/// token pricing. They carry no latency or quality information, so those remain the responsibility
/// of a selection policy. Mapping is advisory only; the routing mechanism never interprets the result.
/// The same options apply to every <see cref="ChatModelCatalog"/> parser.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatModelCatalogOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether only chat-capable entries are mapped.
    /// </summary>
    /// <remarks>
    /// For the LiteLLM catalog this keeps only entries whose <c>mode</c> is <c>"chat"</c>; for the
    /// GitHub Models catalog it keeps only entries whose supported output modalities include
    /// <c>"text"</c> (skipping embedding models).
    /// </remarks>
    /// <value>The default is <see langword="true"/>.</value>
    public bool ChatModelsOnly { get; set; } = true;

    /// <summary>Gets or sets the time the catalog snapshot was produced.</summary>
    /// <remarks>
    /// When set, the value is recorded on each mapped model's <see cref="RoutingChatModel.UpdatedAt"/> so
    /// that a selection policy can reason about how current the metadata is.
    /// </remarks>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Gets or sets the source used for entries that do not carry their own source value.</summary>
    public Uri? SourceUri { get; set; }

    /// <summary>Gets or sets an optional predicate, keyed by model name, used to include or exclude entries.</summary>
    /// <remarks>
    /// The predicate is applied to each model's resolved <see cref="RoutingChatModel.Name"/> (the bare
    /// model name, without any publisher prefix). When <see langword="null"/>, all entries that pass the
    /// other filters are included.
    /// </remarks>
    public Predicate<string>? IncludeModel { get; set; }
}
