// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>
/// A candidate <see cref="IChatClient"/> that a <see cref="SelectingChatClient"/> may route requests to.
/// </summary>
[Experimental(DiagnosticIds.Experiments.AIAutoSelectingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class SelectingChatClientCandidate
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SelectingChatClientCandidate"/> class.
    /// </summary>
    /// <param name="name">A name identifying this candidate.</param>
    /// <param name="client">The <see cref="IChatClient"/> to forward requests to.</param>
    /// <param name="providerName">Optional provider name backing <paramref name="client"/>.</param>
    /// <param name="modelId">Optional model identifier applied when the request does not specify one.</param>
    public SelectingChatClientCandidate(string name, IChatClient client, string? providerName = null, string? modelId = null)
    {
        Name = Throw.IfNullOrWhitespace(name);
        Client = Throw.IfNull(client);
        ProviderName = providerName;
        ModelId = modelId;
    }

    /// <summary>Gets the name identifying this candidate.</summary>
    public string Name { get; }

    /// <summary>Gets the <see cref="IChatClient"/> that requests are forwarded to.</summary>
    public IChatClient Client { get; }

    /// <summary>Gets the optional provider name backing <see cref="Client"/>.</summary>
    public string? ProviderName { get; }

    /// <summary>Gets the optional model identifier applied when the request does not specify one.</summary>
    public string? ModelId { get; }
}
