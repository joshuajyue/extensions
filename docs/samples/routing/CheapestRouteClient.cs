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

/// <summary>Application-owned cost and context configuration for a route name.</summary>
public sealed record RouteCostConfiguration(
    int? ContextWindowTokens,
    decimal? InputCostPerMillionTokens);

/// <summary>
/// Returns the cheapest route that fits the request, and — because selection and fallback are the same
/// method — forms a cost-ordered fallback chain automatically. On every call it filters to routes whose
/// context window admits the prompt, orders the remaining unattempted routes by input price, and returns the
/// cheapest. Cost and context are application policy, so the caller supplies typed configuration keyed by
/// <see cref="ChatRoute.Name"/> rather than storing it on the route.
/// </summary>
public sealed class CheapestRouteClient : RoutingChatClient
{
    private const int CharactersPerToken = 4;
    private readonly IReadOnlyDictionary<string, RouteCostConfiguration> _configurationByRouteName;

    public CheapestRouteClient(
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyDictionary<string, RouteCostConfiguration> configurationByRouteName)
        : base(routes)
    {
        _configurationByRouteName = configurationByRouteName ??
            throw new ArgumentNullException(nameof(configurationByRouteName));

        if (routes.Any(route => !_configurationByRouteName.ContainsKey(route.Name)))
        {
            throw new ArgumentException(
                "Every route must have cost and context configuration.",
                nameof(configurationByRouteName));
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
            .Select(route => (Route: route, Configuration: _configurationByRouteName[route.Name]))
            .Where(candidate =>
                candidate.Configuration.ContextWindowTokens is null ||
                candidate.Configuration.ContextWindowTokens >= approxTokens)
            .OrderBy(candidate =>
                candidate.Configuration.InputCostPerMillionTokens ?? decimal.MaxValue)
            .Select(candidate => candidate.Route)
            .FirstOrDefault();

        return new(next);
    }
}
