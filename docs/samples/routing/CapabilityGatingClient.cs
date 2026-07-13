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

/// <summary>Capabilities policy configuration can assign to a route, and a request can require.</summary>
[Flags]
public enum ModelCapabilities
{
    /// <summary>No special capability.</summary>
    None = 0,

    /// <summary>The route can call tools/functions supplied on <see cref="ChatOptions.Tools"/>.</summary>
    ToolCalling = 1 << 0,

    /// <summary>The route accepts image input.</summary>
    Vision = 1 << 1,

    /// <summary>The route honors a structured (JSON schema) response format.</summary>
    StructuredOutput = 1 << 2,
}

/// <summary>
/// A hard capability gate: it inspects each request for the features it actually needs — tools, image
/// input, structured output — and returns the first unattempted route configured for all of them. Unlike
/// the cost or difficulty policies, capability is a <em>correctness</em> filter, not a preference: a request
/// carrying a tool must never reach a route that cannot call tools. Capabilities are application policy,
/// so the caller supplies typed configuration keyed by <see cref="ChatRoute.Name"/> rather than storing it
/// on the route. Because selection and fallback are the same method, a failure simply advances to the next
/// capable route.
/// </summary>
public sealed class CapabilityGatingClient : RoutingChatClient
{
    private readonly IReadOnlyDictionary<string, ModelCapabilities> _capabilitiesByRouteName;

    public CapabilityGatingClient(
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyDictionary<string, ModelCapabilities> capabilitiesByRouteName)
        : base(routes)
    {
        _capabilitiesByRouteName = capabilitiesByRouteName ??
            throw new ArgumentNullException(nameof(capabilitiesByRouteName));
    }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        ModelCapabilities required = RequiredCapabilities(messages, options);

        ChatRoute? next = routes
            .Except(attempted)
            .FirstOrDefault(route => Supports(route.Name, required));

        // next is null when no capable route remains: the base class throws on the first call, or rethrows
        // the last exception on a fallback call. That is the correct outcome — silently sending a request to
        // an incapable route would be worse than surfacing "no route can serve this".
        return new(next);
    }

    /// <summary>Derives the capabilities a request hard-requires from its messages and options.</summary>
    public static ModelCapabilities RequiredCapabilities(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ModelCapabilities required = ModelCapabilities.None;

        if (options?.Tools is { Count: > 0 })
        {
            required |= ModelCapabilities.ToolCalling;
        }

        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            required |= ModelCapabilities.StructuredOutput;
        }

        if (HasImageContent(messages))
        {
            required |= ModelCapabilities.Vision;
        }

        return required;
    }

    private static bool HasImageContent(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                bool isImage = content switch
                {
                    DataContent data => data.HasTopLevelMediaType("image"),
                    UriContent uri => uri.HasTopLevelMediaType("image"),
                    _ => false,
                };

                if (isImage)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A route satisfies the request when its configured capabilities include every required capability
    // (superset). A route with no configuration is treated as text-only.
    private bool Supports(string routeName, ModelCapabilities required)
    {
        ModelCapabilities available =
            _capabilitiesByRouteName.TryGetValue(routeName, out ModelCapabilities capabilities)
                ? capabilities
                : ModelCapabilities.None;

        return (available & required) == required;
    }
}
