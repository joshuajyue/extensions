// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Describes a route that a <see cref="RoutingChatClient"/> can dispatch to.</summary>
/// <remarks>
/// A route is a named invocation target: a required <see cref="IChatClient"/> plus optional model and reasoning
/// defaults. Multiple routes may bind the same client with different defaults, allowing one provider client to
/// expose several logical model or reasoning profiles. Other policy data — such as cost, capability, context
/// window, provider, latency, or provenance — is application-owned and can be stored alongside routes by the
/// <see cref="RoutingChatClient"/> subclass.
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class ChatRoute
{
    /// <summary>Initializes a new instance of the <see cref="ChatRoute"/> class.</summary>
    /// <param name="name">A stable name identifying this route.</param>
    /// <param name="client">The chat client to invoke when this route is selected.</param>
    /// <param name="modelId">Optional provider-specific model identifier.</param>
    /// <param name="reasoningEffort">Optional reasoning effort to request when this route is selected.</param>
    public ChatRoute(
        string name,
        IChatClient client,
        string? modelId = null,
        ReasoningEffort? reasoningEffort = null)
    {
        Name = Throw.IfNullOrWhitespace(name);
        Client = Throw.IfNull(client);
        ModelId = modelId;
        ReasoningEffort = reasoningEffort;
    }

    /// <summary>Gets the stable name identifying this route.</summary>
    public string Name { get; }

    /// <summary>Gets the chat client to invoke when this route is selected.</summary>
    public IChatClient Client { get; }

    /// <summary>Gets the optional provider-specific model identifier.</summary>
    /// <remarks>
    /// Along with <see cref="ReasoningEffort"/>, this is applied as a request default by
    /// <see cref="RoutingChatClient"/>. An explicit value supplied by the caller takes precedence.
    /// </remarks>
    public string? ModelId { get; }

    /// <summary>Gets the optional reasoning effort to request when this route is selected.</summary>
    /// <remarks>
    /// Along with <see cref="ModelId"/>, this is applied as a request default by <see cref="RoutingChatClient"/>.
    /// An explicit value supplied by the caller takes precedence.
    /// </remarks>
    public ReasoningEffort? ReasoningEffort { get; }
}
