// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Describes objective capability flags that a selection policy may use as a hard capability gate when routing between chat models.</summary>
/// <remarks>
/// Each trait corresponds to a capability that can be read straight from a provider catalog such as
/// LiteLLM's <c>model_prices_and_context_window.json</c> (a <c>supports_*</c> flag). Traits are
/// advisory metadata: the routing mechanism itself never interprets them; only a selection policy
/// (an <see cref="IChatRouteSelector"/>) decides whether and how to use them. They are intended to be
/// used as a <i>capability gate</i> — a correctness filter that answers "can this model do what the
/// request needs at all?" (for example tool calling or vision) — and not as a proxy for quality or
/// performance: modern chat models largely share the same capability flags, so traits cannot
/// discriminate between models on quality. Subjective dimensions (such as a quality score or a
/// "coding" judgement) are deliberately not modeled here — put those in
/// <see cref="RoutingChatModel.AdditionalProperties"/> instead.
/// </remarks>
[Flags]
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public enum RoutingChatModelTraits
{
    /// <summary>No traits are specified.</summary>
    None = 0,

    /// <summary>The model supports tool use or function calling (LiteLLM <c>supports_function_calling</c>/<c>supports_tool_choice</c>).</summary>
    ToolCalling = 1 << 0,

    /// <summary>The model supports vision inputs (LiteLLM <c>supports_vision</c>/<c>supports_image_input</c>).</summary>
    Vision = 1 << 1,

    /// <summary>The model supports reasoning (LiteLLM <c>supports_reasoning</c>).</summary>
    Reasoning = 1 << 2,
}
