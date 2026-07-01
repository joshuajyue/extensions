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
    /// <param name="models">The models available to route to.</param>
    public ChatRouteContext(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<RoutingChatModel> models)
    {
        Messages = Throw.IfNull(messages);
        Options = options;
        Models = Throw.IfNull(models);
    }

    /// <summary>Gets the chat messages being routed.</summary>
    public IEnumerable<ChatMessage> Messages { get; }

    /// <summary>Gets the chat options for the request, if any.</summary>
    public ChatOptions? Options { get; }

    /// <summary>Gets the models available to route to.</summary>
    public IReadOnlyList<RoutingChatModel> Models { get; }
}
