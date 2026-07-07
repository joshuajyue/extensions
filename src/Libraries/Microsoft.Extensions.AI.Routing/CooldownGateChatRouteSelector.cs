// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A decorating selection policy that hides routes currently cooling in a <see cref="RouteCooldownStore"/> from
/// an inner <see cref="IChatRouteSelector"/>, so a cooled route is not chosen while it recovers from failures.
/// </summary>
/// <remarks>
/// <para>
/// It removes every candidate for which <see cref="RouteCooldownStore.IsCooled"/> is <see langword="true"/> and
/// passes the survivors — the same <see cref="ChatRoute"/> instances, preserving the reference identity the
/// router matches on — to the inner selector. If every route is cooling it falls through with the full set
/// rather than stranding the request. Pair it with an <c>onFailure</c> delegate on the routing chat client that
/// writes to the same store (for example cooling a route on an HTTP 429) to close the loop: the delegate cools,
/// this gate skips.
/// </para>
/// <para>
/// This gate only filters the candidates the SELECTOR sees. The router's <c>onFailure</c> delegate looks ahead
/// over the remaining registered routes, so a cooling route can still be re-introduced as a fallback. To keep a
/// cooling route from being attempted at all, ALSO have that <c>onFailure</c> delegate drop cooled routes, for
/// example <c>ctx =&gt; ctx.Remaining.Where(r =&gt; !cooldowns.IsCooled(r.Name)).ToList()</c>.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class CooldownGateChatRouteSelector : IChatRouteSelector
{
    private readonly RouteCooldownStore _cooldowns;
    private readonly IChatRouteSelector _inner;

    /// <summary>Initializes a new instance of the <see cref="CooldownGateChatRouteSelector"/> class.</summary>
    /// <param name="cooldowns">The store recording which routes are currently cooling.</param>
    /// <param name="inner">The selection policy applied to the routes that are not cooling.</param>
    public CooldownGateChatRouteSelector(RouteCooldownStore cooldowns, IChatRouteSelector inner)
    {
        _cooldowns = Throw.IfNull(cooldowns);
        _inner = Throw.IfNull(inner);
    }

    /// <inheritdoc/>
    public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(context);

        List<ChatRoute>? healthy = null;
        foreach (ChatRoute route in context.Routes)
        {
            if (!_cooldowns.IsCooled(route.Name))
            {
                (healthy ??= new List<ChatRoute>(context.Routes.Count)).Add(route);
            }
        }

        // If every route is cooling, fall through with the original set rather than stranding the request.
        IReadOnlyList<ChatRoute> candidates = healthy is { Count: > 0 } ? healthy : context.Routes;
        var gated = new ChatRouteContext(context.Messages, context.Options, candidates);
        return _inner.SelectRouteAsync(gated, cancellationToken);
    }
}
