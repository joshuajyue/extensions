// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Illustrative cookbook sample. Not part of the build; see docs/routing-chat-client-cookbook.md.
#pragma warning disable MEAI001 // Routing types are experimental.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.AI.Samples.Routing;

/// <summary>Application-owned cost and context metadata stored on a route.</summary>
public sealed record RouteCostConfiguration(
    int? ContextWindowTokens,
    decimal? InputCostPerMillionTokens);

/// <summary>
/// Returns the cheapest route that fits the request, and — because selection and fallback are the same
/// method — forms a cost-ordered fallback chain automatically. On every call it filters to routes whose
/// context window admits the prompt, orders the remaining unattempted routes by input price, and returns the
/// cheapest. Cost and context are application policy stored as a strongly typed value in
/// <see cref="ChatRoute.AdditionalProperties"/>.
/// </summary>
public sealed class CheapestRouteClient : RoutingChatClient
{
    /// <summary>The route metadata key under which callers store <see cref="RouteCostConfiguration"/>.</summary>
    public const string ConfigurationKey = "cost";

    private const int CharactersPerToken = 4;

    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes)
        : base(routes)
    {
        if (routes.Any(route => !TryGetConfiguration(route, out _)))
        {
            throw new ArgumentException("Every route must carry cost and context metadata.", nameof(routes));
        }
    }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        int approxTokens = messages.Sum(m => m.Text.Length) / CharactersPerToken;

        ChatRoute? next = routes
            .Except(attempted)
            .Select(route =>
            {
                _ = TryGetConfiguration(route, out RouteCostConfiguration? configuration);
                return (Route: route, Configuration: configuration!);
            })
            .Where(candidate =>
                candidate.Configuration.ContextWindowTokens is null ||
                candidate.Configuration.ContextWindowTokens >= approxTokens)
            .OrderBy(candidate =>
                candidate.Configuration.InputCostPerMillionTokens ?? decimal.MaxValue)
            .Select(candidate => candidate.Route)
            .FirstOrDefault();

        return new(next);
    }

    private static bool TryGetConfiguration(ChatRoute route, out RouteCostConfiguration? configuration) =>
        route.AdditionalProperties?.TryGetValue(ConfigurationKey, out configuration) == true;
}
