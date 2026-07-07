// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides lookup for known chat route metadata used when configuring a <c>RoutingChatClient</c>.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class ChatRouteCatalog
{
    private readonly Dictionary<string, ChatRoute> _entries;

    /// <summary>Initializes a new instance of the <see cref="ChatRouteCatalog"/> class.</summary>
    /// <param name="entries">The catalog entries.</param>
    public ChatRouteCatalog(IEnumerable<ChatRoute> entries)
    {
        _ = Throw.IfNull(entries);

        _entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (ChatRoute entry in entries)
        {
            _ = Throw.IfNull(entry);
            if (_entries.ContainsKey(entry.Name))
            {
                Throw.ArgumentException(nameof(entries), $"A catalog entry named '{entry.Name}' has already been added.");
            }

            _entries.Add(entry.Name, entry);
        }

        Entries = new ReadOnlyCollection<ChatRoute>([.. _entries.Values]);
    }

    /// <summary>Gets the entries in this catalog.</summary>
    public IReadOnlyList<ChatRoute> Entries { get; }

    /// <summary>Gets the catalog entry with the specified name.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <returns>The matching catalog entry.</returns>
    /// <exception cref="KeyNotFoundException">No catalog entry exists for <paramref name="name"/>.</exception>
    public ChatRoute Get(string name)
    {
        _ = Throw.IfNullOrWhitespace(name);

        return _entries.TryGetValue(name, out ChatRoute? entry) ?
            entry :
            throw new KeyNotFoundException($"No chat route catalog entry named '{name}' was found.");
    }

    /// <summary>Attempts to get the catalog entry with the specified name.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="entry">The matching catalog entry, if found.</param>
    /// <returns><see langword="true"/> if a matching entry was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out ChatRoute? entry)
    {
        _ = Throw.IfNullOrWhitespace(name);

        return _entries.TryGetValue(name, out entry);
    }

    /// <summary>Creates a route for the catalog entry with the specified name, bound to a chat client.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="client">The chat client to use when the route is selected.</param>
    /// <returns>A route associated with the specified chat client.</returns>
    public ChatRoute CreateRoute(string name, IChatClient client) =>
        Get(name).WithClient(client);
}
