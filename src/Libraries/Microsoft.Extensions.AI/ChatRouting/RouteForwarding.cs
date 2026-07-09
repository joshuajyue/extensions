// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Extensions.AI;

/// <summary>
/// The one place <see cref="RoutingChatClient"/> shapes a chosen route's request. The router calls this before
/// forwarding to the route's bound client so request-shaping stays in a single place a new dimension can be added
/// to once. The routing loop itself reads route metadata only and never touches options; shaping lives here.
/// </summary>
internal static class RouteForwarding
{
    // Forwards the caller's options on a clone (never mutating the caller's instance), supplying the chosen route's
    // advisory ModelId and ReasoningEffort — but only where the caller did not already pin them, so an explicit
    // request always wins over a route default. When the route adds nothing (no such metadata, or the caller pinned
    // everything), the caller's options are forwarded as-is with no allocation.
    public static ChatOptions? Apply(ChatRoute route, ChatOptions? options)
    {
        bool needsModelId = route.ModelId is not null && options?.ModelId is null;
        bool needsEffort = route.ReasoningEffort is not null && options?.Reasoning?.Effort is null;
        if (!needsModelId && !needsEffort)
        {
            return options;
        }

        ChatOptions forwarded = options?.Clone() ?? new ChatOptions();
        if (needsModelId)
        {
            forwarded.ModelId = route.ModelId;
        }

        if (needsEffort)
        {
            (forwarded.Reasoning ??= new ReasoningOptions()).Effort = route.ReasoningEffort;
        }

        return forwarded;
    }
}
