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
/// An <see cref="IChatClient"/> that routes each request to one of several inner models chosen by a subclass's
/// selection policy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutingChatClient"/> is the routing <em>mechanism</em>: it owns the candidate routes, drives the
/// per-request attempt loop, walks fallbacks on failure, and stamps the chosen route onto the response. It holds
/// no opinion about which model is better — that is entirely delegated to a subclass through the single
/// <see cref="SelectNextRouteAsync"/> method (the <em>policy</em>). Everything a routing decision needs — which
/// route to try first, whether to narrow the candidates, and what to try next after a failure — collapses into
/// that one method: the base calls it once to choose the primary route, then again after each pre-commit failure
/// to choose a fallback, passing the routes already attempted and the exception that just occurred.
/// </para>
/// <para>
/// The method returns the next route to attempt, or <see langword="null"/> to stop. When it returns
/// <see langword="null"/> on the very first call, the router has no route to invoke and throws. When it returns
/// <see langword="null"/> after one or more failures, the router rethrows the last exception. A returned route
/// must be one of the registered <see cref="Routes"/> (matched by reference identity); a route that was already
/// attempted terminates the loop, so routing always terminates no matter what a subclass returns. A cancellation
/// is never treated as a route failure and never reaches the policy. For streaming this applies only before the
/// first update is produced; once a token is on the wire the router never re-routes.
/// </para>
/// <para>
/// Because each route is bound to its own <see cref="IChatClient"/>, a routing pipeline forms a tree: a route's
/// client may have its own middleware, or may itself be another <see cref="RoutingChatClient"/>.
/// </para>
/// <para>
/// Use <see cref="FailoverChatClient"/> for the built-in, opinion-free policy: honor an explicit
/// <see cref="ChatOptions.ModelId"/> else the first route, then each remaining route in order on failure.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public abstract class RoutingChatClient : IChatClient
{
    /// <summary>
    /// The key under which the selected route's name — its router-local alias, not necessarily a provider model
    /// identifier — is stamped onto a response. Use it to see <em>which route</em> answered; for cost or usage
    /// attribution use <see cref="SelectedModelIdKey"/> instead, since a route name can be an arbitrary alias,
    /// can be reused across providers, and need not match any billable model.
    /// </summary>
    public const string SelectedRouteNameKey = "routing.selected_route";

    /// <summary>
    /// The key under which the selected route's underlying provider model identifier is stamped onto a response.
    /// This is the concrete model the request was billed against, so it — not <see cref="SelectedRouteNameKey"/>,
    /// which is a router-local alias — is the correct signal for cost and usage attribution. May be absent when
    /// the chosen client does not expose a model identifier.
    /// </summary>
    public const string SelectedModelIdKey = "routing.selected_model_id";

    /// <summary>The key under which the selected model's provider name is stamped onto a response.</summary>
    public const string SelectedProviderNameKey = "routing.selected_provider";

    /// <summary>
    /// The key under which the full winning route path is stamped onto a response, outermost first and the
    /// concrete leaf model last (for example <c>"Complexity/gpt-4o-mini"</c>). Where <see cref="SelectedRouteNameKey"/>
    /// answers <em>who</em> produced the response (always the leaf), this answers <em>how it got there</em>. When
    /// routers nest, each router prepends the model it selected as the response unwinds, so the path grows one
    /// segment per level to any depth. For a single (non-nested) router the path equals the selected route name.
    /// </summary>
    public const string SelectedPathKey = "routing.selected_path";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivityEvent"/> the router adds to <see cref="System.Diagnostics.Activity.Current"/> for each
    /// model it attempts. Read these events with an <see cref="System.Diagnostics.ActivityListener"/> (or any OpenTelemetry trace
    /// exporter) to observe the full per-request attempt timeline — the order models were tried, which failed
    /// and why, and how long each took.
    /// </summary>
    public const string AttemptEventName = "routing.attempt";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivityEvent"/> the router adds to <see cref="System.Diagnostics.Activity.Current"/> once per
    /// request describing the routing decision: the route selected first for the request. Read it with an
    /// <see cref="System.Diagnostics.ActivityListener"/> or any OpenTelemetry trace exporter.
    /// </summary>
    public const string DecisionEventName = "routing.decision";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> under which the router opens one span per
    /// routed request. Subscribe an <see cref="System.Diagnostics.ActivityListener"/> (or any OpenTelemetry trace exporter) to this
    /// source to observe routing as a span tree: each <see cref="RoutingChatClient"/> — including one nested inside
    /// another as a route's client — gets its own span, so its <see cref="DecisionEventName"/>/<see cref="AttemptEventName"/>
    /// events and per-attempt ordinals are scoped to that span and never collide with an enclosing router's.
    /// Sampling is decided here, per source and process-wide, so a whole tree of nested routers samples uniformly —
    /// there is no per-instance tracing switch. When nothing subscribes, no span is created and the events fall back
    /// to the ambient <see cref="System.Diagnostics.Activity.Current"/> (the flat-router behavior), so identity and path stamped onto
    /// the response — <see cref="SelectedRouteNameKey"/> and <see cref="SelectedPathKey"/> — remain available without
    /// any tracing configured.
    /// </summary>
    public const string ActivitySourceName = "Microsoft.Extensions.AI.Routing";

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

    // The name of the span a router opens per request. See ActivitySourceName for how nesting composes.
    private const string RouteActivityName = "routing.route";

    private const string NoRouteMessage = "Routing produced no route to invoke.";

    // The metadata surfaced via GetService<ChatClientMetadata>(). The provider is the fixed synthetic
    // "routing" rather than any inner model's provider: the router fans out to N providers (and may even
    // cross providers within a single request via fallback), so there is no single honest provider name to
    // report at this layer. The real per-request provider/model is reported by the selected route's own
    // pipeline; DefaultModelId is null because the model is chosen per request. Without this, wrapping the
    // router directly in UseOpenTelemetry() would leave its span with no provider attribution.
    private static readonly ChatClientMetadata _metadata = new(providerName: "routing");

    // One ActivitySource shared by every routing instance (whether a span is created is decided by process-wide
    // listeners keyed on ActivitySourceName, not by any per-instance flag). Opening a child span per router is what
    // lets nested routers form a span tree instead of clobbering one another's events on a single shared
    // activity. When unsubscribed, StartActivity returns null at effectively zero cost and the events fall back to
    // the ambient activity, preserving the original flat-router behavior.
    private static readonly ActivitySource _activitySource = new(ActivitySourceName);

    // The routes this router owns, retained so Dispose can dispose each bound client exactly once and so the
    // selection policy can be handed the registered set as a read-only list.
    private readonly ChatRoute[] _routes;
    private readonly ReadOnlyCollection<ChatRoute> _routeList;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClient"/> class.</summary>
    /// <param name="routes">The routes to dispatch between. At least one is required, each bound to an <see cref="IChatClient"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routes"/> is empty, contains a duplicate name, or contains a route with no bound client.</exception>
    protected RoutingChatClient(IReadOnlyList<ChatRoute> routes)
    {
        _routes = ValidateRoutes(routes);
        _routeList = new ReadOnlyCollection<ChatRoute>(_routes);
    }

    /// <summary>Gets the registered routes this router dispatches between.</summary>
    protected IReadOnlyList<ChatRoute> Routes => _routeList;

    /// <summary>
    /// When overridden in a derived class, chooses the next route to attempt for a request, or <see langword="null"/>
    /// to stop routing.
    /// </summary>
    /// <param name="messages">The chat messages being routed.</param>
    /// <param name="options">The chat options for the request, if any.</param>
    /// <param name="routes">The registered routes available to dispatch to. A returned route must be one of these instances (matched by reference identity).</param>
    /// <param name="attempted">
    /// The routes already attempted this request, in attempt order. Empty on the first call (the initial selection);
    /// on later calls the last entry is the route that just failed. Returning a route already present here stops
    /// routing, guaranteeing termination.
    /// </param>
    /// <param name="lastException">
    /// <see langword="null"/> on the first call. On later calls, the unclassified exception the most recently
    /// attempted route threw before committing any output. The policy owns any interpretation of it — for example
    /// mapping an HTTP status code to a cooldown, or pruning every route that shares the failed route's provider on
    /// an authentication error.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// The next route to attempt — one of <paramref name="routes"/> not already in <paramref name="attempted"/> —
    /// or <see langword="null"/> to stop. Returning <see langword="null"/> on the first call throws, as the router
    /// has no route to invoke; returning <see langword="null"/> after a failure rethrows <paramref name="lastException"/>.
    /// </returns>
    protected abstract ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken);

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);
        messages = NormalizeMessages(messages);

        // Open this router's own span (child of any enclosing router's span). Null when unsubscribed, in which
        // case the decision/attempt events below fall back to the ambient Activity.Current.
        using Activity? routeActivity = _activitySource.StartActivity(RouteActivityName);

        var attempted = new List<ChatRoute>();
        ChatRoute? route = await SelectRouteAsync(messages, options, attempted, lastException: null, cancellationToken);
        if (route is null)
        {
            throw new InvalidOperationException(NoRouteMessage);
        }

        RecordDecisionEvent(route);

        while (true)
        {
            attempted.Add(route);
            int attempt = attempted.Count;
            long startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                ChatResponse response = await route.Client!.GetResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken);
                RecordAttemptEvent(attempt, route, AttemptOutcomeSuccess, startTimestamp, error: null);
                StampResponse(response, route);
                return response;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ChatRoute? next = await SelectRouteAsync(messages, options, attempted, ex, cancellationToken);
                if (next is null)
                {
                    RecordAttemptEvent(attempt, route, AttemptOutcomeError, startTimestamp, ex);
                    throw;
                }

                RecordAttemptEvent(attempt, route, AttemptOutcomeFallback, startTimestamp, ex);
                route = next;
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(messages);
        messages = NormalizeMessages(messages);

        // Open this router's own span (child of any enclosing router's span). Null when unsubscribed, in which
        // case the decision/attempt events below fall back to the ambient Activity.Current.
        using Activity? routeActivity = _activitySource.StartActivity(RouteActivityName);

        var attempted = new List<ChatRoute>();
        ChatRoute? route = await SelectRouteAsync(messages, options, attempted, lastException: null, cancellationToken);
        if (route is null)
        {
            throw new InvalidOperationException(NoRouteMessage);
        }

        RecordDecisionEvent(route);

        while (true)
        {
            attempted.Add(route);
            int attempt = attempted.Count;
            long startTimestamp = Stopwatch.GetTimestamp();

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                route.Client!.GetStreamingResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken).GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                // Failure handling applies only until the first update is produced.
                hasFirst = await enumerator.MoveNextAsync();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ChatRoute? next = await SelectRouteAsync(messages, options, attempted, ex, cancellationToken);
                RecordAttemptEvent(attempt, route, next is null ? AttemptOutcomeError : AttemptOutcomeFallback, startTimestamp, ex);
                await enumerator.DisposeAsync();

                if (next is null)
                {
                    throw;
                }

                route = next;
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

    /// <inheritdoc/>
    public virtual object? GetService(Type serviceType, object? serviceKey = null)
    {
        _ = Throw.IfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Disposes the current instance and all route chat clients, ensuring that each client is disposed only once.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the current instance and all route chat clients, ensuring that each client is disposed only once.</summary>
    /// <param name="disposing"><see langword="true"/> if being called from <see cref="Dispose()"/>; otherwise, <see langword="false"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            HashSet<IChatClient> disposedClients = [];
            foreach (ChatRoute route in _routes)
            {
                IChatClient client = route.Client!;
                if (disposedClients.Add(client))
                {
                    client.Dispose();
                }
            }
        }
    }

    // Invokes the subclass policy for one selection, then validates the result: null passes through (the caller
    // interprets it as "no route" or "stop"), a route not among the registered set throws (routes are matched by
    // reference identity), and a route already attempted collapses to null so the loop always terminates no matter
    // what a subclass returns.
    private async ValueTask<ChatRoute?> SelectRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        List<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        ChatRoute? route = await SelectNextRouteAsync(messages, options, _routeList, attempted, lastException, cancellationToken);
        if (route is null)
        {
            return null;
        }

        _ = ValidateRoutedRoute(route);
        return attempted.Contains(route) ? null : route;
    }

    private static ChatRoute[] ValidateRoutes(IReadOnlyList<ChatRoute> routes)
    {
        _ = Throw.IfNull(routes);

        if (routes.Count == 0)
        {
            Throw.ArgumentException(nameof(routes), "At least one route must be provided.");
        }

        var result = new ChatRoute[routes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            result[i] = Throw.IfNull(routes[i]);
            if (result[i].Client is null)
            {
                Throw.ArgumentException(nameof(routes), $"The route '{result[i].Name}' must be bound to an IChatClient.");
            }

            for (int j = 0; j < i; j++)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(result[j].Name, result[i].Name))
                {
                    Throw.ArgumentException(nameof(routes), $"A route named '{result[i].Name}' has already been added.");
                }
            }
        }

        return result;
    }

    private ChatRoute ValidateRoutedRoute(ChatRoute route)
    {
        if (Array.IndexOf(_routes, route) < 0)
        {
            Throw.InvalidOperationException(
                $"The {nameof(RoutingChatClient)} policy must route to one of the registered routes.");
        }

        return route;
    }

    private static IEnumerable<ChatMessage> NormalizeMessages(IEnumerable<ChatMessage> messages) =>
        messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();

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
        if (!props.ContainsKey(SelectedRouteNameKey))
        {
            props[SelectedRouteNameKey] = route.Name;
            props[SelectedModelIdKey] = route.ModelId;
            props[SelectedProviderNameKey] = route.ProviderName;
        }

        props[SelectedPathKey] = props.TryGetValue(SelectedPathKey, out string? prior) && prior is not null
            ? $"{route.Name}/{prior}"
            : route.Name;
    }

    private static void StampActivity(ChatRoute route)
    {
        Activity? activity = Activity.Current;
        if (activity is not null)
        {
            _ = activity.SetTag(SelectedRouteNameKey, route.Name);
            if (route.ModelId is not null)
            {
                _ = activity.SetTag(SelectedModelIdKey, route.ModelId);
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

        _ = activity.AddEvent(new ActivityEvent(AttemptEventName, tags: tags));
    }

    // Elapsed time since a Stopwatch.GetTimestamp() reading, using the framework helper where available.
    private static TimeSpan GetElapsedTime(long startingTimestamp) =>
#if NET
        Stopwatch.GetElapsedTime(startingTimestamp);
#else
        new((long)((Stopwatch.GetTimestamp() - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
#endif

    // Adds one routing.decision event to the ambient span describing the route selected first for the request.
    // Fires once per request. No-op unless a listener is recording the current activity.
    private static void RecordDecisionEvent(ChatRoute route)
    {
        Activity? activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            [SelectedRouteNameKey] = route.Name,
        };

        _ = activity.AddEvent(new ActivityEvent(DecisionEventName, tags: tags));
    }
}
