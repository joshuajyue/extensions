// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A thread-safe, in-memory record of routes that are temporarily "cooling down" — for example after an
/// HTTP 429 or 503 — keyed by <see cref="ChatRoute.Name"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately minimal building block, not a policy. It stores, per route name, an instant until
/// which the route should be considered unavailable, and answers whether a route is currently cooling. It has
/// no notion of failure taxonomy, thresholds, failure windows, half-open probing, or automatic success
/// tracking — those are opinions the caller owns. Compose it however you like:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// The <b>write half</b> is typically the <c>onFailure</c> delegate passed to a routing chat client: on a
/// failure you deem transient, call <see cref="Cool"/> for the failed route (optionally deriving the duration
/// from a <c>Retry-After</c> header) and return the remaining routes to fall through to the next candidate.
/// </description>
/// </item>
/// <item>
/// <description>
/// The <b>read half</b> is typically the routing chat client's <c>canRoute</c> candidate filter, which admits a
/// route only when <see cref="IsCooled"/> returns <see langword="false"/> for it (for example
/// <c>canRoute: (route, _, _) =&gt; !cooldowns.IsCooled(route.Name)</c>), so a cooling route is not chosen again
/// until its window elapses.
/// </description>
/// </item>
/// <item>
/// <description>
/// Call <see cref="Clear"/> to end a route's cooldown early — for instance from a success hook once the route
/// responds normally again.
/// </description>
/// </item>
/// </list>
/// <para>
/// Route names are compared case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>) to match the
/// router's own route lookup. Expiry is evaluated lazily against the injectable clock — nothing runs on a
/// timer and entries are only overwritten or cleared, so the store never removes expired entries on its own;
/// its footprint is bounded by the number of distinct route names it has seen. Because state lives only in
/// this process, each replica cools independently; supply a shared backing store yourself if you need
/// cross-process coordination.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class RouteCooldownStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooledUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="RouteCooldownStore"/> class.</summary>
    /// <param name="now">
    /// An optional clock used to stamp and evaluate cooldown windows. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>. Inject a fake clock to make cooldown-dependent code deterministic
    /// in tests.
    /// </param>
    public RouteCooldownStore(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Marks <paramref name="routeName"/> as cooling for the given <paramref name="duration"/> from now.</summary>
    /// <param name="routeName">The <see cref="ChatRoute.Name"/> to cool. Compared case-insensitively.</param>
    /// <param name="duration">
    /// How long the route should remain cooling. A non-positive duration clears immediately (the route is never
    /// reported as cooling), which callers can use to represent "no cooldown".
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is <see langword="null"/> or empty.</exception>
    /// <remarks>Calling this again for the same route replaces any existing window rather than extending it.</remarks>
    public void Cool(string routeName, TimeSpan duration)
    {
        _ = Throw.IfNullOrEmpty(routeName);
        _cooledUntil[routeName] = _now() + duration;
    }

    /// <summary>Gets a value indicating whether <paramref name="routeName"/> is currently cooling.</summary>
    /// <param name="routeName">The <see cref="ChatRoute.Name"/> to test. Compared case-insensitively.</param>
    /// <returns>
    /// <see langword="true"/> if the route was cooled and its window has not yet elapsed; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is <see langword="null"/> or empty.</exception>
    public bool IsCooled(string routeName)
    {
        _ = Throw.IfNullOrEmpty(routeName);
        return _cooledUntil.TryGetValue(routeName, out DateTimeOffset until) && _now() < until;
    }

    /// <summary>Ends any cooldown for <paramref name="routeName"/>, making it immediately eligible again.</summary>
    /// <param name="routeName">The <see cref="ChatRoute.Name"/> to clear. Compared case-insensitively.</param>
    /// <returns><see langword="true"/> if a cooldown entry was removed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is <see langword="null"/> or empty.</exception>
    public bool Clear(string routeName)
    {
        _ = Throw.IfNullOrEmpty(routeName);
        return _cooledUntil.TryRemove(routeName, out _);
    }
}
