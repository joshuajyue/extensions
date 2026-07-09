// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Illustrative cookbook sample. Not part of the build; see docs/routing-chat-client-cookbook.md.
#pragma warning disable MEAI001 // Routing types are experimental.

using System;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.AI.Samples.Routing;

/// <summary>
/// Ordered failover that additionally puts a route on a cooldown when it rate-limits, and skips cooled routes
/// while they cool. Filtering is simply not returning a route: a cooled route is omitted from selection until
/// its cool-until time passes. Cooldowns self-expire, so no success signal is needed to reinstate a route.
/// </summary>
/// <remarks>
/// The router is a DI singleton, so the cooldown map persists across requests — writes made while handling one
/// request are visible to the next. On failure the policy reads a <c>Retry-After</c> header off a
/// <see cref="ClientResultException"/> (how MEAI's OpenAI adapter surfaces HTTP errors) and records a cool-until
/// time for the route that just failed before choosing the next.
/// </remarks>
public sealed class CooldownRoutingClient : RoutingChatClient
{
    private static readonly TimeSpan _defaultCooldown = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _coolUntil = new(StringComparer.OrdinalIgnoreCase);

    public CooldownRoutingClient(IReadOnlyList<ChatRoute> routes)
        : base(routes)
    {
    }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // On a failure, cool the route that just failed (attempted[^1]) for its Retry-After, or a default.
        if (lastException is not null && attempted.Count > 0)
        {
            ChatRoute failed = attempted[attempted.Count - 1];
            _coolUntil[failed.Name] = DateTimeOffset.UtcNow + (RetryAfter(lastException) ?? _defaultCooldown);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Prefer routes that are not cooling; skip anything already attempted this request.
        ChatRoute? next = routes
            .Except(attempted)
            .FirstOrDefault(r => !IsCooling(r.Name, now));

        // If every remaining route is cooling, fall back to the soonest-available one rather than giving up.
        next ??= routes
            .Except(attempted)
            .OrderBy(r => _coolUntil.TryGetValue(r.Name, out DateTimeOffset until) ? until : DateTimeOffset.MinValue)
            .FirstOrDefault();

        return new(next);
    }

    private bool IsCooling(string routeName, DateTimeOffset now) =>
        _coolUntil.TryGetValue(routeName, out DateTimeOffset until) && until > now;

    private static TimeSpan? RetryAfter(Exception exception)
    {
        if (exception is ClientResultException clientResult &&
            clientResult.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string? value) == true)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
            {
                return when - DateTimeOffset.UtcNow;
            }
        }

        return null;
    }
}
