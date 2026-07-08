// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides extensions for adding a <see cref="DelegatingRoutingChatClient"/> to a <see cref="ChatClientBuilder"/> pipeline.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public static class DelegatingRoutingChatClientBuilderExtensions
{
    /// <summary>
    /// Adds routing over a single inner client: a selector chooses a route per request and the router shapes the
    /// request with that route's <see cref="ChatRoute.ModelId"/> and <see cref="ChatRoute.ReasoningEffort"/> before
    /// forwarding to the next client in the pipeline.
    /// </summary>
    /// <param name="builder">The <see cref="ChatClientBuilder"/>.</param>
    /// <param name="routes">The routes to dispatch between. At least one is required; each is metadata only.</param>
    /// <param name="selector">The selection policy, or <see langword="null"/> for the opinion-free default.</param>
    /// <param name="onFailure">An optional failure delegate consulted on a pre-commit dispatch failure.</param>
    /// <param name="capabilityDetector">An optional capability detector narrowing candidate routes.</param>
    /// <returns>The <paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ChatClientBuilder UseRouting(
        this ChatClientBuilder builder,
        IReadOnlyList<ChatRoute> routes,
        IChatRouteSelector? selector = null,
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure = null,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>>? capabilityDetector = null)
    {
        _ = Throw.IfNull(builder);

        return builder.Use(innerClient =>
            new DelegatingRoutingChatClient(innerClient, routes, selector, onFailure, capabilityDetector));
    }
}
