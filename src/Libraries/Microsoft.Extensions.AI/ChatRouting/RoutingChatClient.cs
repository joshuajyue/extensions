// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;

#pragma warning disable SA1204 // Static members should appear before non-static members

namespace Microsoft.Extensions.AI;

/// <summary>
/// An <see cref="IChatClient"/> that routes each request to one of several inner models chosen by a
/// swappable selection policy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutingChatClient"/> is the routing <em>mechanism</em>: it owns the candidate models,
/// runs the selector once per request, walks fallbacks on failure, and stamps the chosen model onto the
/// response. It holds no opinion about which model is better —
/// that is entirely delegated to an <see cref="IChatRouteSelector"/> (the <em>policy</em>). When no
/// selector is supplied, the default is deterministic and opinion-free: it honors
/// <see cref="ChatOptions.ModelId"/> when set, otherwise it uses the first registered model.
/// </para>
/// <para>
/// Before a selector runs, the router applies a soft <em>capability gate</em>: it narrows the candidate
/// routes to those that can satisfy capabilities the request provably needs. The required capabilities are
/// produced by an injectable <em>capability detector</em>; the default detector requires
/// <see cref="ChatModelCapabilities.Vision"/> when a message carries image content and
/// <see cref="ChatModelCapabilities.FunctionCalling"/> when <see cref="ChatOptions.Tools"/> include an
/// <see cref="AIFunctionDeclaration"/>. A route declares the tokens it supports under
/// <see cref="ChatModelCapabilities.PropertyKey"/> in its <see cref="ChatRoute.AdditionalProperties"/>, and the
/// router keeps only routes whose declared set is a superset of the required set. This is a correctness filter
/// shared by every selector and the fallback chain — not a quality signal. It is soft: when no registered route
/// declares a required capability, the gate falls through to the full set rather than stranding the request, and
/// it can be disabled by supplying a <c>capabilityDetector</c> that always returns no tokens.
/// </para>
/// <para>
/// A selector returns a <see cref="ChatRoutePlan"/> of the models it prefers, primary first. The router
/// attempts those in order. When a route's dispatch fails before any output is committed, an optional
/// <em>failure delegate</em> (the <c>onFailure</c> constructor parameter) is consulted with the failed route,
/// the unclassified exception, and the untried candidates still available; it returns the routes to try next,
/// in order — any subset of the survivors, optionally reintroducing a capable candidate the plan omitted — or
/// an empty result to stop and rethrow. This single hook owns continue-versus-stop, fallback ordering, and
/// blast-radius pruning (for example dropping every route that shares the failed route's provider on an
/// authentication error), so a selector that naturally picks one model (for example a complexity classifier)
/// can stay out of the failure business entirely and let the router own it. When <c>onFailure</c> is
/// <see langword="null"/>, the router walks the plan's routes in order and rethrows once they are exhausted.
/// Because returned routes are de-duplicated against those already attempted, routing always terminates. The
/// router holds no error taxonomy of its own: the delegate owns any interpretation of the exception.
/// </para>
/// <para>
/// Because each candidate is itself an <see cref="IChatClient"/>, a routing pipeline forms a tree:
/// a candidate may have its own middleware, or may itself be another <see cref="RoutingChatClient"/>.
/// </para>
/// </remarks>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public class RoutingChatClient : IChatClient
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
    /// request describing the routing decision: the selected model and any decision-rationale a selector attached
    /// via <see cref="ChatRoutePlan.DecisionMetadata"/> (for example a complexity tier or a semantic similarity
    /// score). Read it with an <see cref="System.Diagnostics.ActivityListener"/> or any OpenTelemetry trace exporter.
    /// </summary>
    public const string DecisionEventName = "routing.decision";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> under which the router opens one span per
    /// routed request. Subscribe an <see cref="System.Diagnostics.ActivityListener"/> (or any OpenTelemetry trace exporter) to this
    /// source to observe routing as a span tree: each <see cref="RoutingChatClient"/> — including one nested inside
    /// another as a candidate model — gets its own span, so its <see cref="DecisionEventName"/>/<see cref="AttemptEventName"/>
    /// events and per-attempt ordinals are scoped to that span and never collide with an enclosing router's.
    /// Sampling is decided here, per source and process-wide, so a whole tree of nested routers samples uniformly —
    /// there is no per-instance tracing switch. When nothing subscribes, no span is created and the events fall back
    /// to the ambient <see cref="System.Diagnostics.Activity.Current"/> (the flat-router behavior), so identity and path stamped onto
    /// the response — <see cref="SelectedRouteNameKey"/> and <see cref="SelectedPathKey"/> — remain available without
    /// any tracing configured.
    /// </summary>
    public const string ActivitySourceName = "Microsoft.Extensions.AI.Routing";

    // The metadata surfaced via GetService<ChatClientMetadata>(). The provider is the fixed synthetic
    // "routing" rather than any inner model's provider: the router fans out to N providers (and may even
    // cross providers within a single request via fallback), so there is no single honest provider name to
    // report at this layer. The real per-request provider/model is reported by the selected branch's own
    // pipeline; DefaultModelId is null because the model is chosen per request. Without this, wrapping the
    // router directly in UseOpenTelemetry() would leave its span with no provider attribution.
    private static readonly ChatClientMetadata _metadata = new(providerName: "routing");

    // The per-route dispatch closures handed to the shared RouteDispatchLoop. They are the only place this front
    // door differs from any other routing surface: given the engine's chosen route and the caller's normalized
    // messages/options, each forwards to that route's own bound IChatClient after applying the route's advisory
    // options (via the shared RouteForwarding.Apply) when the caller did not pin them. Cached as static readonly to
    // avoid per-request allocation; they close over nothing.
    private static readonly Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _dispatch =
        static (route, messages, options, cancellationToken) =>
            route.Client!.GetResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken);

    private static readonly Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamingDispatch =
        static (route, messages, options, cancellationToken) =>
            route.Client!.GetStreamingResponseAsync(messages, RouteForwarding.Apply(route, options), cancellationToken);

    // The routes this router owns, retained so Dispose can dispose each bound client exactly once. Every routing
    // decision — the selector call, the capability gate, the fallback walk, and all telemetry — lives in the
    // engine, which this router drives with the dispatch closures above.
    private readonly ChatRoute[] _routes;
    private readonly RouteDispatchLoop _loop;

    /// <summary>Initializes a new instance of the <see cref="RoutingChatClient"/> class.</summary>
    /// <param name="routes">The routes to dispatch between. At least one is required, each bound to an <see cref="IChatClient"/>.</param>
    /// <param name="selector">The selection policy, or <see langword="null"/> for the opinion-free default.</param>
    /// <param name="onFailure">
    /// An optional failure delegate consulted when a route's dispatch throws before any output is committed. It
    /// receives a <see cref="RouteFailureContext"/> (the failed route, the unclassified exception, the attempt
    /// count, and the untried candidates still available in <see cref="RouteFailureContext.Remaining"/>) and
    /// returns the routes to try next, in order: any subset of the survivors, in any order, optionally
    /// reintroducing a capable candidate the plan omitted. Returning <see langword="null"/> or an empty list
    /// stops routing and rethrows the exception. The delegate is invoked on every pre-commit failure, including
    /// the final route's (so a stateful delegate — for example one that cools a route on an HTTP 429 — observes
    /// every failure); returned routes are de-duplicated against those already attempted, so a returned route
    /// that was already tried is dropped and routing always terminates. When <see langword="null"/>, the router
    /// walks the plan's routes in order and rethrows once they are exhausted — the historical default. The
    /// exception is passed unclassified: the delegate owns any status-code interpretation. For streaming this
    /// applies only before the first update is produced; once a token is on the wire the router never re-routes.
    /// A cancellation is never treated as a route failure and never reaches the delegate.
    /// </param>
    /// <param name="capabilityDetector">
    /// An optional capability detector. Given the request messages and options, it returns the capability tokens the
    /// request provably requires; the router then narrows the candidate routes to those whose declared capabilities
    /// (under <see cref="ChatModelCapabilities.PropertyKey"/> in <see cref="ChatRoute.AdditionalProperties"/>) are a
    /// superset. When <see langword="null"/>, a default detector is used that requires
    /// <see cref="ChatModelCapabilities.Vision"/> for image content and <see cref="ChatModelCapabilities.FunctionCalling"/>
    /// for <see cref="AIFunctionDeclaration"/> tools. The gate is soft: if no registered route declares a required
    /// capability, it falls through to the full set rather than stranding the request. Supply a detector that always
    /// returns no tokens to disable the gate so every registered route is always a candidate.
    /// </param>
    public RoutingChatClient(
        IReadOnlyList<ChatRoute> routes,
        IChatRouteSelector? selector = null,
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure = null,
        Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>>? capabilityDetector = null)
    {
        _routes = ValidateRoutes(routes);
        _loop = new RouteDispatchLoop(_routes, selector, onFailure, capabilityDetector);
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _loop.RunAsync(messages, options, _dispatch, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _loop.RunStreamingAsync(messages, options, _streamingDispatch, cancellationToken);

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

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        IChatRouteSelector? selector = _loop.Selector;
        return selector is not null && serviceType.IsInstanceOfType(selector) ? selector : null;
    }

    /// <summary>Disposes the current instance and all model chat clients, ensuring that each client is disposed only once.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the current instance and all model chat clients, ensuring that each client is disposed only once.</summary>
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
}

#pragma warning disable SA1402 // File may only contain a single type — the failure-delegate vocabulary lives beside the mechanism that consults it.

/// <summary>
/// The inputs a <see cref="RoutingChatClient"/> failure delegate receives when a route's dispatch throws before
/// any output is committed, together with the untried routes still available to attempt. The exception is passed
/// unclassified: the delegate owns any interpretation of it — for example mapping an HTTP status code to a
/// cooldown, or pruning every route that shares the failed route's provider on an authentication error.
/// </summary>
[Experimental(DiagnosticIds.Experiments.AIRoutingChat, UrlFormat = DiagnosticIds.UrlFormat)]
public sealed class RouteFailureContext
{
    /// <summary>Initializes a new instance of the <see cref="RouteFailureContext"/> class.</summary>
    /// <param name="route">The route whose dispatch threw.</param>
    /// <param name="exception">The exception the route threw, unclassified.</param>
    /// <param name="attemptNumber">The 1-based count of routes attempted so far this request, including this one.</param>
    /// <param name="remaining">The untried routes still available to attempt, in the router's default order.</param>
    /// <param name="options">The chat options for the request, if any.</param>
    /// <param name="messages">The chat messages being routed.</param>
    public RouteFailureContext(
        ChatRoute route,
        Exception exception,
        int attemptNumber,
        IReadOnlyList<ChatRoute> remaining,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages)
    {
        Route = route;
        Exception = exception;
        AttemptNumber = attemptNumber;
        Remaining = remaining;
        Options = options;
        Messages = messages;
    }

    /// <summary>Gets the route whose dispatch threw.</summary>
    public ChatRoute Route { get; }

    /// <summary>Gets the exception the route threw, unclassified.</summary>
    public Exception Exception { get; }

    /// <summary>Gets the 1-based count of routes attempted so far this request, including the one that just failed.</summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// Gets the untried routes still available to attempt, in the router's default order: the current plan's
    /// remaining routes first, then any other capable candidate the plan omitted. The failure delegate returns
    /// the routes to try next — any subset of these, in any order, is valid; returning an empty list (or
    /// <see langword="null"/>) stops routing and rethrows. Returned routes are de-duplicated and any already
    /// attempted are dropped, so the router always terminates.
    /// </summary>
    public IReadOnlyList<ChatRoute> Remaining { get; }

    /// <summary>Gets the chat options for the request, if any.</summary>
    public ChatOptions? Options { get; }

    /// <summary>Gets the chat messages being routed.</summary>
    public IEnumerable<ChatMessage> Messages { get; }
}
