// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides the inputs a <see cref="IChatRouteSelector"/> uses to produce a <see cref="ChatRoutePlan"/>.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatRouteContext
{
    /// <summary>Initializes a new instance of the <see cref="ChatRouteContext"/> class.</summary>
    /// <param name="messages">The chat messages being routed.</param>
    /// <param name="options">The chat options for the request, if any.</param>
    /// <param name="routes">The routes available to dispatch to.</param>
    public ChatRouteContext(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes)
    {
        Messages = Throw.IfNull(messages);
        Options = options;
        Routes = Throw.IfNull(routes);
    }

    /// <summary>Gets the chat messages being routed.</summary>
    public IEnumerable<ChatMessage> Messages { get; }

    /// <summary>Gets the chat options for the request, if any.</summary>
    public ChatOptions? Options { get; }

    /// <summary>Gets the routes available to dispatch to.</summary>
    /// <remarks>
    /// A selector resolves a route by matching against these instances — for example
    /// <c>Routes.FirstOrDefault(r =&gt; string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))</c>.
    /// The router matches routes by reference identity, so a <see cref="ChatRoutePlan"/> must contain instances
    /// drawn from this list; reconstructing a <see cref="ChatRoute"/> with identical metadata would make the
    /// router throw.
    /// </remarks>
    public IReadOnlyList<ChatRoute> Routes { get; }
}
