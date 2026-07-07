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
    /// The name of the <see cref="ActivityEvent"/> the router adds to <see cref="Activity.Current"/> for each
    /// model it attempts. Read these events with an <see cref="ActivityListener"/> (or any OpenTelemetry trace
    /// exporter) to observe the full per-request attempt timeline — the order models were tried, which failed
    /// and why, and how long each took.
    /// </summary>
    public const string AttemptEventName = "routing.attempt";

    /// <summary>
    /// The name of the <see cref="ActivityEvent"/> the router adds to <see cref="Activity.Current"/> once per
    /// request describing the routing decision: the selected model and any decision-rationale a selector attached
    /// via <see cref="ChatRoutePlan.DecisionMetadata"/> (for example a complexity tier or a semantic similarity
    /// score). Read it with an <see cref="ActivityListener"/> or any OpenTelemetry trace exporter.
    /// </summary>
    public const string DecisionEventName = "routing.decision";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> under which the router opens one span per
    /// routed request. Subscribe an <see cref="ActivityListener"/> (or any OpenTelemetry trace exporter) to this
    /// source to observe routing as a span tree: each <see cref="RoutingChatClient"/> — including one nested inside
    /// another as a candidate model — gets its own span, so its <see cref="DecisionEventName"/>/<see cref="AttemptEventName"/>
    /// events and per-attempt ordinals are scoped to that span and never collide with an enclosing router's.
    /// Sampling is decided here, per source and process-wide, so a whole tree of nested routers samples uniformly —
    /// there is no per-instance tracing switch. When nothing subscribes, no span is created and the events fall back
    /// to the ambient <see cref="Activity.Current"/> (the flat-router behavior), so identity and path stamped onto
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

    // One ActivitySource shared by every RoutingChatClient instance (whether a span is created is decided by
    // process-wide listeners keyed on ActivitySourceName, not by any per-instance flag). Opening a child span per
    // router is what lets nested routers form a span tree instead of clobbering one another's events on a single
    // shared activity; when unsubscribed, StartActivity returns null at effectively zero cost and the events fall
    // back to the ambient activity, preserving the original flat-router behavior.
    private static readonly ActivitySource _activitySource = new(ActivitySourceName);

    // The metadata surfaced via GetService<ChatClientMetadata>(). The provider is the fixed synthetic
    // "routing" rather than any inner model's provider: the router fans out to N providers (and may even
    // cross providers within a single request via fallback), so there is no single honest provider name to
    // report at this layer. The real per-request provider/model is reported by the selected branch's own
    // pipeline; DefaultModelId is null because the model is chosen per request. Without this, wrapping the
    // router directly in UseOpenTelemetry() would leave its span with no provider attribution.
    private static readonly ChatClientMetadata _metadata = new(providerName: "routing");

    private readonly ChatRoute[] _routes;
    private readonly ReadOnlyCollection<ChatRoute> _routeList;
    private readonly IChatRouteSelector? _selector;
    private readonly Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? _onFailure;
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>> _capabilityDetector;

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
        _routeList = new ReadOnlyCollection<ChatRoute>(_routes);
        _selector = selector;
        _onFailure = onFailure;
        _capabilityDetector = capabilityDetector ?? DefaultCapabilityDetector;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
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
            ChatOptions? forwarded = CreateForwardedOptions(route, options);
            long startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                ChatResponse response = await route.Client!.GetResponseAsync(normalizedMessages, forwarded, cancellationToken);
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

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            ChatOptions? forwarded = CreateForwardedOptions(route, options);
            long startTimestamp = Stopwatch.GetTimestamp();

            IAsyncEnumerator<ChatResponseUpdate> enumerator =
                route.Client!.GetStreamingResponseAsync(normalizedMessages, forwarded, cancellationToken).GetAsyncEnumerator(cancellationToken);

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

        return _selector is not null && serviceType.IsInstanceOfType(_selector) ? _selector : null;
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

    private ValueTask<ChatRoutePlan> RunSelectorAsync(ChatRouteContext context, CancellationToken cancellationToken) =>
        _selector is null
            ? new ValueTask<ChatRoutePlan>(DefaultSelectRoute(context))
            : _selector.SelectRouteAsync(context, cancellationToken);

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

    private static ChatOptions? CreateForwardedOptions(ChatRoute route, ChatOptions? options)
    {
        // The router forwards the caller's options on a clone (never mutating the caller's instance), supplying
        // the chosen provider model id when the caller did not pin one via ChatOptions.ModelId. When no
        // adjustment is needed, the caller's options are forwarded as-is.
        if (route.ModelId is null || options?.ModelId is not null)
        {
            return options;
        }

        ChatOptions forwarded = options?.Clone() ?? new ChatOptions();
        forwarded.ModelId = route.ModelId;
        return forwarded;
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
            [SelectedRouteNameKey] = plan.OrderedRoutes[0].Name,
        };

        if (plan.DecisionMetadata is { } metadata)
        {
            foreach (KeyValuePair<string, object> entry in metadata)
            {
                tags[entry.Key] = entry.Value;
            }
        }

        _ = activity.AddEvent(new ActivityEvent(DecisionEventName, tags: tags));
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
