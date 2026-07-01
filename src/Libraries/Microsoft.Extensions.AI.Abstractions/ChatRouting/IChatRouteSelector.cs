// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;

namespace Microsoft.Extensions.AI;

/// <summary>Represents a swappable selection policy that decides how a <c>RoutingChatClient</c> routes a request.</summary>
/// <remarks>
/// The routing mechanism (<c>RoutingChatClient</c>) is intentionally opinion-free; all
/// knowledge about which model is better, or what the user is asking for, lives in an
/// implementation of this interface. Use <see cref="ChatRouteSelector.Create(System.Func{ChatRouteContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask{ChatRoutePlan}})"/>
/// for inline delegates, or one of the built-in policies such as <c>ComplexityChatRouteSelector</c>
/// or <c>SemanticChatRouteSelector</c>.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public interface IChatRouteSelector
{
    /// <summary>Produces a routing plan for the supplied context.</summary>
    /// <param name="context">The inputs available for the decision.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A plan whose first model is the primary route and whose remaining models are fallbacks.</returns>
    ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default);
}
