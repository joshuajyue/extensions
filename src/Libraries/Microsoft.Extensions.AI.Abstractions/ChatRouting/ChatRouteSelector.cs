// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides factory methods for creating <see cref="IChatRouteSelector"/> instances from delegates.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public static class ChatRouteSelector
{
    /// <summary>Creates a selector from an asynchronous delegate.</summary>
    /// <param name="selector">The delegate that produces a plan for a given context.</param>
    /// <returns>An <see cref="IChatRouteSelector"/> that invokes <paramref name="selector"/>.</returns>
    public static IChatRouteSelector Create(Func<ChatRouteContext, CancellationToken, ValueTask<ChatRoutePlan>> selector) =>
        new DelegatingChatRouteSelector(Throw.IfNull(selector));

    /// <summary>Creates a selector from a synchronous delegate.</summary>
    /// <param name="selector">The delegate that produces a plan for a given context.</param>
    /// <returns>An <see cref="IChatRouteSelector"/> that invokes <paramref name="selector"/>.</returns>
    public static IChatRouteSelector Create(Func<ChatRouteContext, ChatRoutePlan> selector)
    {
        _ = Throw.IfNull(selector);
        return new DelegatingChatRouteSelector((context, _) => new ValueTask<ChatRoutePlan>(selector(context)));
    }

    private sealed class DelegatingChatRouteSelector(Func<ChatRouteContext, CancellationToken, ValueTask<ChatRoutePlan>> selector) : IChatRouteSelector
    {
        public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default) =>
            selector(Throw.IfNull(context), cancellationToken);
    }
}
