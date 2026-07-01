// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

namespace Microsoft.Extensions.AI;

/// <summary>Provides lookup for known chat model metadata used when configuring a <c>RoutingChatClient</c>.</summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class RoutingChatModelCatalog
{
    private readonly Dictionary<string, RoutingChatModel> _entries;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatModelCatalog"/> class.</summary>
    /// <param name="entries">The catalog entries.</param>
    public RoutingChatModelCatalog(IEnumerable<RoutingChatModel> entries)
    {
        _ = Throw.IfNull(entries);

        _entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingChatModel entry in entries)
        {
            _ = Throw.IfNull(entry);
            if (_entries.ContainsKey(entry.Name))
            {
                Throw.ArgumentException(nameof(entries), $"A catalog entry named '{entry.Name}' has already been added.");
            }

            _entries.Add(entry.Name, entry);
        }

        Entries = new ReadOnlyCollection<RoutingChatModel>([.. _entries.Values]);
    }

    /// <summary>Gets the entries in this catalog.</summary>
    public IReadOnlyList<RoutingChatModel> Entries { get; }

    /// <summary>Gets the catalog entry with the specified name.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <returns>The matching catalog entry.</returns>
    /// <exception cref="KeyNotFoundException">No catalog entry exists for <paramref name="name"/>.</exception>
    public RoutingChatModel Get(string name)
    {
        _ = Throw.IfNullOrWhitespace(name);

        return _entries.TryGetValue(name, out RoutingChatModel? entry) ?
            entry :
            throw new KeyNotFoundException($"No routing chat model catalog entry named '{name}' was found.");
    }

    /// <summary>Attempts to get the catalog entry with the specified name.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="entry">The matching catalog entry, if found.</param>
    /// <returns><see langword="true"/> if a matching entry was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out RoutingChatModel? entry)
    {
        _ = Throw.IfNullOrWhitespace(name);

        return _entries.TryGetValue(name, out entry);
    }

    /// <summary>Creates a model for the catalog entry with the specified name, bound to a chat client.</summary>
    /// <param name="name">The catalog entry name.</param>
    /// <param name="client">The chat client to use when the model is selected.</param>
    /// <returns>A model associated with the specified chat client.</returns>
    public RoutingChatModel CreateModel(string name, IChatClient client) =>
        Get(name).WithClient(client);
}
