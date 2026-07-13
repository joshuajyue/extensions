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

/// <summary>
/// Returns the cheapest route that fits the request, and — because selection and fallback are the same
/// method — forms a cost-ordered fallback chain automatically. On every call it filters to routes whose
/// context window admits the prompt, orders the remaining unattempted routes by input price, and returns the
/// cheapest. It reads the advisory <see cref="ChatRoute"/> cost and context-window metadata that a basic
/// ordered-failover policy ignores.
/// </summary>
public sealed class CheapestRouteClient : RoutingChatClient
{
    private const int CharactersPerToken = 4;

    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes)
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
        int approxTokens = messages.Sum(m => m.Text.Length) / CharactersPerToken;

        ChatRoute? next = routes
            .Except(attempted)
            .Where(r => r.MaxInputTokens is null || r.MaxInputTokens >= approxTokens) // context-window filter
            .OrderBy(r => r.InputTokenCostPerMillion ?? decimal.MaxValue)             // cheapest first
            .FirstOrDefault();

        return new(next);
    }
}
