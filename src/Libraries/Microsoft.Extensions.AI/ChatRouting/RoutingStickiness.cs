// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Specifies how a <see cref="RoutingChatClient"/> caches and reuses a routing decision across requests.</summary>
/// <remarks>
/// Stickiness is a pure caching <em>scope</em> owned by the routing mechanism; it carries no opinion
/// about which model is better. The complementary half — deciding when a cached decision should be
/// invalidated — belongs to the selection policy and is expressed by
/// <see cref="ChatRoutePlan.RemainsValid"/>.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public enum RoutingStickiness
{
    /// <summary>Run the selector for every request.</summary>
    EveryCall = 0,

    /// <summary>Run the selector once per <see cref="RoutingChatClient"/> instance and reuse the result for all requests.</summary>
    PerInstance = 1,

    /// <summary>
    /// Run the selector once per conversation and reuse the result for subsequent requests in that conversation.
    /// The conversation key is read from the <see cref="RoutingChatClient.ConversationKeyPropertyName"/> entry in
    /// <see cref="ChatOptions.AdditionalProperties"/>. When no key can be resolved, behavior falls back to
    /// <see cref="EveryCall"/>.
    /// </summary>
    /// <remarks>
    /// This scope is deliberately decoupled from <see cref="ChatOptions.ConversationId"/>. That value is a provider
    /// state token — absent for stateless providers and changing on every turn for some stateful ones — which makes
    /// it unfit as a stable routing-stickiness key. The conversation key is instead owned by the caller.
    /// </remarks>
    ByConversation = 2,
}
