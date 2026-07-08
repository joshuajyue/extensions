// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

#pragma warning disable SA1204 // Static members should appear before non-static members

namespace Microsoft.Extensions.AI;

/// <summary>
/// The client-agnostic engine shared by the routing front doors. It owns everything a routed request needs
/// <em>except</em> how a chosen route is actually invoked: the capability gate, the one selector call per
/// request, the fallback walk, the dedup/termination bookkeeping, and all telemetry. It reads only route
/// <em>metadata</em> (name, model id, provider) and never touches option shaping or client dispatch — each
/// front door supplies those as a <c>dispatch</c> closure, the sole per-front-door seam.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutingChatClient"/> — the multiplexer that owns N inner clients — delegates its per-request
/// dispatch here, passing a closure that forwards to the chosen route's own <see cref="ChatRoute.Client"/>.
/// A later option-shaping middleware (one inner client, many routes as metadata) can reuse the same loop by
/// passing a closure that forwards to its single inner client after patching the options. Because the loop is
/// identical in both cases, the streaming first-token-commit rule and the fallback semantics live in exactly
/// one place.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
internal sealed class RouteDispatchLoop
{
    // Tag keys carried by each routing.attempt event. Kept private: the event/tag schema is a telemetry detail
    // observed through ActivityListener, not a programmatic API surface.
    private const string AttemptOrdinalKey = "routing.attempt.ordinal";
    private const string AttemptRouteKey = "routing.attempt.route";
    private const string AttemptModelIdKey = "routing.attempt.model_id";
    private const string AttemptProviderKey = "routing.attempt.provider";
    private const string AttemptOutcomeKey = "routing.attempt.outcome";
    private const string AttemptDurationMsKey = "routing.attempt.duration_ms";
    private const string AttemptErrorTypeKey = "routing.attempt.error_type";

    // routing.attempt.outcome values.
    private const string AttemptOutcomeSuccess = "success";   // this model produced a response (or first token)
    private const string AttemptOutcomeFallback = "fallback"; // this model failed; the router fell back to the next
    private const string AttemptOutcomeError = "error";       // this model failed and no fallback remained (propagates)

    // The name of the span a router opens per request. See RoutingChatClient.ActivitySourceName for how nesting composes.
    private const string RouteActivityName = "routing.route";

    // One ActivitySource shared by every routing instance (whether a span is created is decided by process-wide
    // listeners keyed on ActivitySourceName, not by any per-instance flag). Opening a child span per router is what
    // lets nested routers form a span tree instead of clobbering one another's events on a single shared
    // activity. When unsubscribed, StartActivity returns null at effectively zero cost and the events fall back to
    // the ambient activity, preserving the original flat-router behavior.
    private static readonly ActivitySource _activitySource = new(RoutingChatClient.ActivitySourceName);

    private readonly ChatRoute[] _routes;
    private readonly ReadOnlyCollection<ChatRoute> _routeList;
    private readonly Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? _onFailure;
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>> _capabilityDetector;

    internal RouteDispatchLoop(
        ChatRoute[] routes,
        IChatRouteSelector? selector,
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>>? capabilityDetector)
    {
        _routes = routes;
        _routeList = new ReadOnlyCollection<ChatRoute>(_routes);
        Selector = selector;
        _onFailure = onFailure;
        _capabilityDetector = capabilityDetector ?? DefaultCapabilityDetector;
    }

    /// <summary>Gets the selection policy, or <see langword="null"/> for the opinion-free default.</summary>
    internal IChatRouteSelector? Selector { get; }

    // Runs one routed request to completion, invoking the front door's dispatch closure for each attempted route.
    // The engine owns the plan, the fallback walk, and all telemetry; `dispatch` owns only how a chosen route is
    // invoked (and any per-route option shaping) — the single line that differs between front doors.
    internal async Task<ChatResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> dispatch,
        CancellationToken cancellationToken)
    {
        _ = Throw.IfNull(messages);

        // Open this router's own span (child of any enclosing router's span). Null when unsubscribed, in which
        // case the decision/attempt events below fall back to the ambient Activity.Current.
        using Activity? routeActivity = _activitySource.StartActivity(RouteActivityName);

        IEnumerable<ChatMessage> normalizedMessages = NormalizeMessages(messages);
        IReadOnlyList<ChatRoute> candidates = GetCandidateRoutes(normalizedMessages, options);
        var context = new ChatRouteContext(normalizedMessages, options, candidates);
        ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
        RecordDecisionEvent(plan);

        List<ChatRoute> queue = DedupPlan(plan);
        if (queue.Count == 0)
        {
            throw new InvalidOperationException("Routing produced no model to invoke.");
        }

        var tried = new HashSet<ChatRoute>();
        int attempt = 0;
        for (int i = 0; i < queue.Count; i++)
        {
            ChatRoute route = ValidateRoutedRoute(queue[i]);
            if (!tried.Add(route))
            {
                continue; // defensive: a route is attempted at most once per request
            }

            attempt++;
            long startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                ChatResponse response = await dispatch(route, normalizedMessages, options, cancellationToken);
                RecordAttemptEvent(attempt, route, AttemptOutcomeSuccess, startTimestamp, error: null);
                StampResponse(response, route);
                return response;
            }
            catch (Exception ex)
            {
                List<ChatRoute>? next = NextAfterFailure(route, ex, attempt, queue, i, tried, candidates, options, normalizedMessages, cancellationToken);
                if (next is null)
                {
                    RecordAttemptEvent(attempt, route, AttemptOutcomeError, startTimestamp, ex);
                    throw;
                }

                RecordAttemptEvent(attempt, route, AttemptOutcomeFallback, startTimestamp, ex);
                queue = next;
                i = -1; // iterate the freshly-computed queue from the start
            }
        }

        // Unreachable: the loop above returns on success or rethrows when no route remains to try.
        throw new InvalidOperationException("Routing produced no model to invoke.");
    }

    // The streaming counterpart of RunAsync. Failure handling applies only until the first update is produced —
    // once a token is on the wire the engine never re-routes. The dispatch closure yields the chosen route's own
    // stream, so — exactly as in the non-streaming path — it is the only line that differs between front doors.
    internal async IAsyncEnumerable<ChatResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> dispatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = Throw.IfNull(messages);

        // Open this router's own span (child of any enclosing router's span). Null when unsubscribed, in which
        // case the decision/attempt events below fall back to the ambient Activity.Current.
        using Activity? routeActivity = _activitySource.StartActivity(RouteActivityName);

        IEnumerable<ChatMessage> normalizedMessages = NormalizeMessages(messages);
        IReadOnlyList<ChatRoute> candidates = GetCandidateRoutes(normalizedMessages, options);
        var context = new ChatRouteContext(normalizedMessages, options, candidates);
        ChatRoutePlan plan = await RunSelectorAsync(context, cancellationToken);
        RecordDecisionEvent(plan);

        List<ChatRoute> queue = DedupPlan(plan);

        // Mirror the non-streaming path's invariant: a plan always carries at least one model, so an empty
        // attempt order is unreachable today. Guard it anyway so both paths fail identically rather than the
        // stream silently completing empty if a future selector/gate change ever yields zero attempts.
        if (queue.Count == 0)
        {
            throw new InvalidOperationException("Routing produced no model to invoke.");
        }

        var tried = new HashSet<ChatRoute>();
        int attempt = 0;
        for (int i = 0; i < queue.Count; i++)
        {
            ChatRoute route = ValidateRoutedRoute(queue[i]);
            if (!tried.Add(route))
            {
                continue; // defensive: a route is attempted at most once per request
            }

            attempt++;
            long startTimestamp = Stopwatch.GetTimestamp();

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                dispatch(route, normalizedMessages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                // Failure handling applies only until the first update is produced.
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                List<ChatRoute>? next = NextAfterFailure(route, ex, attempt, queue, i, tried, candidates, options, normalizedMessages, cancellationToken);
                RecordAttemptEvent(attempt, route, next is null ? AttemptOutcomeError : AttemptOutcomeFallback, startTimestamp, ex);
                await enumerator.DisposeAsync();

                if (next is null)
                {
                    throw;
                }

                queue = next;
                i = -1; // iterate the freshly-computed queue from the start
                continue;
            }

            // The first token committed this model: from the router's perspective the attempt succeeded, and
            // the recorded duration is its time-to-first-token. Mid-stream failures past here are not a routing
            // fallback decision, so they are not recorded as attempts.
            RecordAttemptEvent(attempt, route, AttemptOutcomeSuccess, startTimestamp, error: null);
            StampActivity(route);
            try
            {
                bool stamped = false;
                while (hasFirst)
                {
                    ChatResponseUpdate update = enumerator.Current;
                    if (!stamped)
                    {
                        StampUpdate(update, route);
                        stamped = true;
                    }

                    yield return update;
                    if (routeActivity is not null)
                    {
                        // Restore this router's span as current after the yield: the async-iterator state machine
                        // can otherwise drop it (https://github.com/dotnet/runtime/issues/47802), which would
                        // misparent a nested router's span on the next MoveNextAsync. Guarded so an unsubscribed
                        // (null) source never clobbers the caller's ambient activity.
                        Activity.Current = routeActivity;
                    }

                    hasFirst = await enumerator.MoveNextAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            yield break;
        }
    }

    private ValueTask<ChatRoutePlan> RunSelectorAsync(ChatRouteContext context, CancellationToken cancellationToken) =>
        Selector is null
            ? new ValueTask<ChatRoutePlan>(DefaultSelectRoute(context))
            : Selector.SelectRouteAsync(context, cancellationToken);

    // The router's capability gate. Narrows the registered routes to those that can satisfy the capabilities the
    // request provably needs, so every selector — and the fallback chain — only ever sees capable candidates. The
    // required tokens come from the injected capability detector; a route declares the tokens it supports under
    // ChatModelCapabilities.PropertyKey in its AdditionalProperties, and a route qualifies only when its declared
    // set is a superset of the required set. The gate is soft: when no registered route positively declares a
    // required capability (sparse metadata), it returns the full set rather than stranding the request. A detector
    // that always returns no tokens disables the gate entirely.
    private ReadOnlyCollection<ChatRoute> GetCandidateRoutes(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        IReadOnlyCollection<string> required = _capabilityDetector(messages, options);
        if (required.Count == 0)
        {
            return _routeList;
        }

        List<ChatRoute>? capable = null;
        foreach (ChatRoute route in _routes)
        {
            if (SupportsAllCapabilities(route, required))
            {
                (capable ??= new List<ChatRoute>(_routes.Length)).Add(route);
            }
        }

        return capable is { Count: > 0 } ? new ReadOnlyCollection<ChatRoute>(capable) : _routeList;
    }

    // The default capability detector. Derives the capabilities a request provably requires using only signals that
    // cannot be wrong about the request itself: a message carrying image content needs a vision route, and supplying
    // function-declaration tools needs a function-calling route. Fuzzier dimensions (such as "reasoning") are
    // deliberately excluded — they are a selector's job to weigh, not a hard correctness gate. Provider-hosted tools
    // (web search, code interpreter) are also excluded: whether they work is a property of the deployment, not the
    // request, so require them via a custom detector when the lineup is heterogeneous in that capability.
    private static IReadOnlyCollection<string> DefaultCapabilityDetector(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        List<string>? required = null;

        if (options?.Tools is { Count: > 0 } tools && tools.OfType<AIFunctionDeclaration>().Any())
        {
            (required ??= []).Add(ChatModelCapabilities.FunctionCalling);
        }

        if (MessagesContainImage(messages))
        {
            (required ??= []).Add(ChatModelCapabilities.Vision);
        }

        return required is not null ? required : Array.Empty<string>();
    }

    // Determines whether a route's declared capability tokens (under ChatModelCapabilities.PropertyKey in its
    // AdditionalProperties) are a superset of the required tokens. A route that declares no tokens can satisfy only
    // a request that requires none, so it never matches a non-empty requirement here.
    private static bool SupportsAllCapabilities(ChatRoute route, IReadOnlyCollection<string> required)
    {
        if (route.AdditionalProperties is null ||
            !route.AdditionalProperties.TryGetValue(ChatModelCapabilities.PropertyKey, out object? value) ||
            value is not IEnumerable<string> tokens)
        {
            return false;
        }

        var supported = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        foreach (string token in required)
        {
            if (!supported.Contains(token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MessagesContainImage(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            IList<AIContent> contents = message.Contents;
            for (int i = 0; i < contents.Count; i++)
            {
                switch (contents[i])
                {
                    case DataContent data when data.HasTopLevelMediaType("image"):
                    case UriContent uri when uri.HasTopLevelMediaType("image"):
                    case HostedFileContent file when file.HasTopLevelMediaType("image"):
                        return true;
                }
            }
        }

        return false;
    }

    // The initial attempt queue: the selected plan's routes in order, de-duplicated so each is attempted at most
    // once. The router never eagerly appends non-plan candidates here — reaching beyond the plan on failure is
    // the failure delegate's job (see NextAfterFailure), so a request with no failures pays nothing for it.
    private static List<ChatRoute> DedupPlan(ChatRoutePlan plan)
    {
        IReadOnlyList<ChatRoute> ordered = plan.OrderedRoutes;
        var seen = new HashSet<ChatRoute>();
        var result = new List<ChatRoute>(ordered.Count);
        foreach (ChatRoute route in ordered)
        {
            if (route is not null && seen.Add(route))
            {
                result.Add(route);
            }
        }

        return result;
    }

    // Decides what to attempt after queue[failedIndex] threw before committing output, or null to rethrow.
    // A cancellation is the caller's decision, not a route failure, so it never falls back. With no failure
    // delegate the historical rule applies: continue through the remaining planned routes, rethrow once none
    // remain. Otherwise the delegate is consulted with the untried candidates (the plan tail first, then any
    // other gated candidate) and returns the routes to try next; its result is validated to registered,
    // not-yet-attempted routes so routing always terminates, and an empty result means rethrow.
    private List<ChatRoute>? NextAfterFailure(
        ChatRoute failedRoute,
        Exception exception,
        int attemptNumber,
        List<ChatRoute> queue,
        int failedIndex,
        HashSet<ChatRoute> tried,
        IReadOnlyList<ChatRoute> candidates,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (_onFailure is null)
        {
            // Historical default: walk the remaining planned routes only, never the non-plan candidates.
            List<ChatRoute> tail = BuildQueueTail(queue, failedIndex + 1, tried);
            return tail.Count > 0 ? tail : null;
        }

        List<ChatRoute> remaining = BuildRemaining(queue, failedIndex + 1, candidates, tried);
        var failureContext = new RouteFailureContext(failedRoute, exception, attemptNumber, remaining, options, messages);
        IReadOnlyList<ChatRoute>? next = _onFailure(failureContext);
        if (next is null)
        {
            return null;
        }

        List<ChatRoute> sanitized = SanitizeNext(next, tried);
        return sanitized.Count > 0 ? sanitized : null;
    }

    // The untried, de-duplicated tail of the current queue, from startIndex onward.
    private static List<ChatRoute> BuildQueueTail(List<ChatRoute> queue, int startIndex, HashSet<ChatRoute> tried)
    {
        var seen = new HashSet<ChatRoute>();
        var tail = new List<ChatRoute>(Math.Max(0, queue.Count - startIndex));
        for (int j = startIndex; j < queue.Count; j++)
        {
            ChatRoute route = queue[j];
            if (!tried.Contains(route) && seen.Add(route))
            {
                tail.Add(route);
            }
        }

        return tail;
    }

    // The failure delegate's lookahead: every untried route still worth attempting, in the router's default
    // order — the current queue's remaining tail first (preserving the planned order), then any other gated
    // candidate the plan omitted. Drawn from the gated candidate set, so capability filtering still holds.
    private static List<ChatRoute> BuildRemaining(List<ChatRoute> queue, int startIndex, IReadOnlyList<ChatRoute> candidates, HashSet<ChatRoute> tried)
    {
        List<ChatRoute> remaining = BuildQueueTail(queue, startIndex, tried);
        var seen = new HashSet<ChatRoute>(remaining);
        foreach (ChatRoute route in candidates)
        {
            if (!tried.Contains(route) && seen.Add(route))
            {
                remaining.Add(route);
            }
        }

        return remaining;
    }

    // Validates the routes a failure delegate returned: each must be a registered route (ValidateRoutedRoute
    // throws otherwise), and any already attempted or duplicated is dropped so the attempt set strictly grows.
    private List<ChatRoute> SanitizeNext(IReadOnlyList<ChatRoute> next, HashSet<ChatRoute> tried)
    {
        var seen = new HashSet<ChatRoute>();
        var result = new List<ChatRoute>(next.Count);
        foreach (ChatRoute route in next)
        {
            if (route is null)
            {
                continue;
            }

            _ = ValidateRoutedRoute(route);
            if (!tried.Contains(route) && seen.Add(route))
            {
                result.Add(route);
            }
        }

        return result;
    }

    // The opinion-free default: honor an explicit ModelId, otherwise the first registered model.
    private static ChatRoutePlan DefaultSelectRoute(ChatRouteContext context)
    {
        string? modelId = context.Options?.ModelId;
        if (!string.IsNullOrEmpty(modelId))
        {
            foreach (ChatRoute route in context.Routes)
            {
                if (string.Equals(route.ModelId, modelId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.Name, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatRoutePlan(route);
                }
            }
        }

        return new ChatRoutePlan(context.Routes[0]);
    }

    private static void StampResponse(ChatResponse response, ChatRoute route)
    {
        if (response is null)
        {
            return;
        }

        StampIdentity(response.AdditionalProperties ??= [], route);
        StampActivity(route);
    }

    private static void StampUpdate(ChatResponseUpdate update, ChatRoute route) =>
        StampIdentity(update.AdditionalProperties ??= [], route);

    // Records "who answered" (identity) and "how it got there" (path) onto a response/update's properties.
    //
    // Identity (SelectedRouteNameKey/SelectedModelIdKey/SelectedProviderNameKey) is written first-writer-wins. When
    // routers nest, the innermost router stamps first — with the concrete leaf route that actually produced the
    // tokens — and any outer router's later stamp over the same object is skipped. This keeps identity leaf-truthful
    // (the "routed to" badge and route-pinning) rather than being overwritten by an intermediate router whose own
    // ModelId/ProviderName are null. Cost/usage attribution should key off SelectedModelIdKey — the concrete billed
    // model — not the route name, which is a router-local alias. The three keys move together so identity is never
    // half-written.
    //
    // Path (SelectedPathKey) is complementary and accumulates: each router prepends the name of the model it
    // selected as the response unwinds, yielding the full winning route to any depth (for example
    // "Complexity/gpt-4o-mini"). Identity answers "who"; path answers "how it got there" — two concerns kept in
    // two keys rather than overloaded onto one scalar, which is what previously corrupted identity under nesting.
    private static void StampIdentity(AdditionalPropertiesDictionary props, ChatRoute route)
    {
        if (!props.ContainsKey(RoutingChatClient.SelectedRouteNameKey))
        {
            props[RoutingChatClient.SelectedRouteNameKey] = route.Name;
            props[RoutingChatClient.SelectedModelIdKey] = route.ModelId;
            props[RoutingChatClient.SelectedProviderNameKey] = route.ProviderName;
        }

        props[RoutingChatClient.SelectedPathKey] = props.TryGetValue(RoutingChatClient.SelectedPathKey, out string? prior) && prior is not null
            ? $"{route.Name}/{prior}"
            : route.Name;
    }

    private static void StampActivity(ChatRoute route)
    {
        Activity? activity = Activity.Current;
        if (activity is not null)
        {
            _ = activity.SetTag(RoutingChatClient.SelectedRouteNameKey, route.Name);
            if (route.ModelId is not null)
            {
                _ = activity.SetTag(RoutingChatClient.SelectedModelIdKey, route.ModelId);
            }
        }
    }

    // Adds one routing.attempt event to the ambient span describing a single attempt: its order, route and model
    // identity, outcome (success/fallback/error), elapsed time, and — on failure — the exception type. This is
    // the per-attempt timeline that StampActivity (winner-only) does not capture. It is a no-op unless a
    // listener is recording the current activity, so the cost is only paid when telemetry is being collected.
    private static void RecordAttemptEvent(int ordinal, ChatRoute route, string outcome, long startTimestamp, Exception? error)
    {
        Activity? activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            [AttemptOrdinalKey] = ordinal,
            [AttemptRouteKey] = route.Name,
            [AttemptOutcomeKey] = outcome,
            [AttemptDurationMsKey] = GetElapsedTime(startTimestamp).TotalMilliseconds,
        };

        if (route.ModelId is not null)
        {
            tags[AttemptModelIdKey] = route.ModelId;
        }

        if (route.ProviderName is not null)
        {
            tags[AttemptProviderKey] = route.ProviderName;
        }

        if (error is not null)
        {
            tags[AttemptErrorTypeKey] = error.GetType().FullName;
        }

        _ = activity.AddEvent(new ActivityEvent(RoutingChatClient.AttemptEventName, tags: tags));
    }

    // Elapsed time since a Stopwatch.GetTimestamp() reading, using the framework helper where available.
    private static TimeSpan GetElapsedTime(long startingTimestamp) =>
#if NET
        Stopwatch.GetElapsedTime(startingTimestamp);
#else
        new((long)((Stopwatch.GetTimestamp() - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
#endif

    // Adds one routing.decision event to the ambient span describing the selected plan: the primary route and any
    // decision-rationale the selector attached (complexity tier, semantic score, ...). Fires once per request.
    // No-op unless a listener is recording the current activity.
    private static void RecordDecisionEvent(ChatRoutePlan plan)
    {
        Activity? activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            [RoutingChatClient.SelectedRouteNameKey] = plan.OrderedRoutes[0].Name,
        };

        if (plan.DecisionMetadata is { } metadata)
        {
            foreach (KeyValuePair<string, object> entry in metadata)
            {
                tags[entry.Key] = entry.Value;
            }
        }

        _ = activity.AddEvent(new ActivityEvent(RoutingChatClient.DecisionEventName, tags: tags));
    }

    private static IEnumerable<ChatMessage> NormalizeMessages(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();

    private ChatRoute ValidateRoutedRoute(ChatRoute route)
    {
        if (route is null || Array.IndexOf(_routes, route) < 0)
        {
            Throw.InvalidOperationException(
                $"The {nameof(RoutingChatClient)} selector must route to one of the registered routes.");
        }

        return route;
    }
}

#pragma warning disable SA1402 // File may only contain a single type — the shared shaping helper lives beside the engine both front doors drive.

/// <summary>
/// The one place a routing front door shapes a chosen route's request. Both front doors — the
/// <see cref="RoutingChatClient"/> multiplexer and the <see cref="DelegatingRoutingChatClient"/> middleware — call
/// this from their dispatch closure so request-shaping stays identical no matter which surface routed. Kept out of
/// <see cref="RouteDispatchLoop"/> on purpose: the engine reads route metadata only and never touches options, so
/// shaping lives here where a new dimension can be added once and both front doors inherit it.
/// </summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
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
