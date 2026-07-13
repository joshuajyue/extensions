// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Illustrative cookbook sample. Not part of the build; see docs/routing-chat-client-cookbook.md.
#pragma warning disable MEAI001 // Routing types are experimental.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.AI.Samples.Routing;

/// <summary>
/// Ordered failover with a per-route circuit breaker: after a route fails a threshold number of times in a row
/// its circuit opens and the route is skipped for a reset window, then allowed a single half-open trial. The
/// breaker is time-based so it needs no success signal — once the reset window elapses the route is eligible
/// again, and its failure streak is cleared when it is next chosen for that half-open trial.
/// </summary>
/// <remarks>
/// The routing seam only observes failures (it is re-invoked with <c>lastException</c> after a route throws), so
/// this breaker closes on a timer rather than on the first success. To close the instant a route recovers, wrap
/// each route's client — or the whole router — in a <see cref="DelegatingChatClient"/> that resets the route's
/// failure count on a successful response.
/// </remarks>
public sealed class CircuitBreakerRoutingClient : RoutingChatClient
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan _openDuration = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Breaker> _breakers = new(StringComparer.OrdinalIgnoreCase);

    public CircuitBreakerRoutingClient(IReadOnlyList<ChatRoute> routes)
        : base(routes)
    {
    }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Record the failure of the route that just failed, opening its circuit once it trips the threshold.
        if (lastException is not null && attempted.Count > 0)
        {
            string failedName = attempted[attempted.Count - 1].Name;
            _breakers.AddOrUpdate(
                failedName,
                _ => new Breaker(1, now + _openDuration),
                (_, existing) =>
                {
                    int failures = existing.Failures + 1;
                    DateTimeOffset openUntil = failures >= FailureThreshold ? now + _openDuration : existing.OpenUntil;
                    return new Breaker(failures, openUntil);
                });
        }

        // Choose the first unattempted route whose circuit is not open (closed, or half-open past its window).
        ChatRoute? next = routes
            .Except(attempted)
            .FirstOrDefault(r => !IsOpen(r.Name, now));

        // Clear the chosen route's streak only when it is a half-open trial (its open window has elapsed), so one
        // stale failure cannot immediately re-trip it. A route still accumulating failures below the threshold keeps
        // its streak, so consecutive failures add up and actually open the circuit.
        if (next is not null && IsHalfOpen(next.Name, now))
        {
            _ = _breakers.TryRemove(next.Name, out _);
        }

        return new(next);
    }

    private bool IsOpen(string routeName, DateTimeOffset now) =>
        _breakers.TryGetValue(routeName, out Breaker breaker) &&
        breaker.Failures >= FailureThreshold &&
        breaker.OpenUntil > now;

    // A route whose circuit tripped but whose open window has since elapsed: eligible for a single half-open trial.
    private bool IsHalfOpen(string routeName, DateTimeOffset now) =>
        _breakers.TryGetValue(routeName, out Breaker breaker) &&
        breaker.Failures >= FailureThreshold &&
        breaker.OpenUntil <= now;

    private readonly record struct Breaker(int Failures, DateTimeOffset OpenUntil);
}
