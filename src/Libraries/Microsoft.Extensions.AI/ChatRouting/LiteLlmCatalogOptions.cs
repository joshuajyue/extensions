// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Options controlling how a LiteLLM catalog document is mapped to <see cref="RoutingChatModel"/> entries.</summary>
/// <remarks>
/// The LiteLLM catalog (<c>model_prices_and_context_window.json</c>) is an external, frequently
/// updated source of objective per-model metadata: capability flags, context windows, and token
/// pricing. It carries no latency or quality information, so those remain the responsibility of a
/// selection policy. Mapping is advisory only; the routing mechanism never interprets the result.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class LiteLlmCatalogOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether only entries whose <c>mode</c> is <c>"chat"</c> are mapped.
    /// </summary>
    /// <value>The default is <see langword="true"/>.</value>
    public bool ChatModelsOnly { get; set; } = true;

    /// <summary>Gets or sets the time the catalog snapshot was produced.</summary>
    /// <remarks>
    /// When set, the value is recorded on each mapped model's <see cref="RoutingChatModel.UpdatedAt"/> so
    /// that a selection policy can reason about how current the metadata is.
    /// </remarks>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Gets or sets the source used for entries that do not carry their own <c>source</c> value.</summary>
    public Uri? SourceUri { get; set; }

    /// <summary>Gets or sets an optional predicate, keyed by model name, used to include or exclude entries.</summary>
    /// <remarks>When <see langword="null"/>, all entries that pass the other filters are included.</remarks>
    public Predicate<string>? IncludeModel { get; set; }
}
