# Route chat requests with `RoutingChatClient`

`RoutingChatClient` is an [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
that owns several candidate routes and forwards each request to one of them. It is an **abstract base
class**: it owns the routing *mechanism* — holding the candidates, dispatching, walking fallbacks, and
recording telemetry — and defers the routing *policy* to a single method you override.

There is only one type to learn:

- **`RoutingChatClient`** (abstract) — the mechanism. Override one method,
  [`SelectNextRouteAsync`](#write-a-policy-derive-from-routingchatclient), to decide which route to try
  next. Selection, filtering, and failover are all expressed through that single method.

Your routing *policy* — route by difficulty, by meaning, by cost, by health, or plain ordered failover —
is whatever you write in that method. The [cookbook](./routing-chat-client-cookbook.md) has a catalog of
ready-to-copy policies; this article describes the type, how to construct a router, how to write a policy,
and how to observe routing decisions.

> [!IMPORTANT]
> The routing types are experimental. Every public type is annotated with
> `[Experimental("MEAI001")]`, so using them produces the `MEAI001` diagnostic, and the API shape may
> change before it stabilizes. Suppress the diagnostic deliberately (for example, with
> `#pragma warning disable MEAI001`) to opt in.

## Packages and namespaces

All routing types live in the `Microsoft.Extensions.AI` namespace, so a single
`using Microsoft.Extensions.AI;` covers them. They ship in two packages:

| Package | Contents |
|---|---|
| `Microsoft.Extensions.AI.Abstractions` | The route metadata type: `ChatRoute`. |
| `Microsoft.Extensions.AI` | The routing mechanism: `RoutingChatClient`. |

## How routing works

Routing is built on **one seam**. Instead of separate concepts for selection, filtering, and fallback,
a router answers a single question, repeatedly, until it has a response:

> Given the request and everything attempted so far (and why the last attempt failed), which route do I
> try next — or `null` to stop?

That question is `RoutingChatClient.SelectNextRouteAsync`. The base class calls it in a loop:

1. **Select.** Call `SelectNextRouteAsync` with an empty `attempted` list and a `null` `lastException`.
   The returned route is dispatched. Returning `null` here throws `InvalidOperationException` (a router
   with nothing to route to).
2. **Dispatch.** Forward the request to the route's `Client`. If it succeeds (or, when streaming,
   produces its first update), the response is stamped and returned.
3. **Fall back.** If the dispatch fails *before* producing output, call `SelectNextRouteAsync` again —
   now with the failed route appended to `attempted` and the thrown exception as `lastException`. The
   returned route is dispatched next. Returning `null` here **rethrows the last exception**.

The three classic routing behaviors are all expressions of this one method:

- **Selection** is the first call (`attempted` is empty).
- **Failover** is a later call (`lastException` is set; `attempted[^1]` is the route that just failed).
- **Filtering** is simply *not returning* a route you don't want to try.

A returned route must be one of the **registered** route instances (matched by reference); returning an
unregistered route throws. A route already present in `attempted` terminates the loop, so a buggy policy
can never spin forever. Cancellation is never treated as a failure and never triggers fallback. When
streaming, fallback applies only until the first update is produced — once a token is on the wire the
router never re-routes.

Because `RoutingChatClient` is itself an `IChatClient`, and each route is bound to an `IChatClient`, a
routing pipeline forms a tree: a route's `Client` can be another `RoutingChatClient`. Route coarsely at
the top (for example, by region or tenant) and finely below (for example, by cost).

## Get started: your first policy

A policy is a `RoutingChatClient` subclass that overrides `SelectNextRouteAsync`. Here is a minimal one
that routes short prompts to a small model and everything else to a large one, then falls back to any
remaining route if the first choice fails — three routing behaviors (selection, filtering, failover) in
one method:

```csharp
using Microsoft.Extensions.AI;

public sealed class BySizeRouter : RoutingChatClient
{
    public BySizeRouter(IReadOnlyList<ChatRoute> routes) : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        if (attempted.Count == 0)
        {
            // First call: pick a route by a property of the request.
            bool small = messages.Sum(m => m.Text.Length) < 280;
            return new(routes.First(r => r.Name == (small ? "small" : "large")));
        }

        // Later call: the previous route failed — try the next one not yet attempted.
        return new(routes.Except(attempted).FirstOrDefault());
    }
}
```

Wire it up by binding each route to an `IChatClient`:

```csharp
IChatClient router = new BySizeRouter(
[
    new ChatRoute("small", modelId: "gpt-4o-mini", client: openaiMini),
    new ChatRoute("large", modelId: "gpt-4o",      client: openai),
]);

ChatResponse response = await router.GetResponseAsync("Hello!"); // short → "small"
```

That is the whole pattern. Real policies differ only in the body of `SelectNextRouteAsync`: a difficulty
classifier, an embedding router, a cooldown gate, or plain ordered failover. The
[cookbook](./routing-chat-client-cookbook.md) has each as a complete, copy-ready sample; the rest of this
article covers the pieces the example above uses.

A `ChatRoute` is the unit a router chooses between. It carries a required `Name` (a stable, router-local
alias) and optional, advisory metadata. The mechanism reads only route identity to dispatch and forward
the `ModelId`; your policy decides how to use the rest.

| Member | Type | Description |
|---|---|---|
| `Name` | `string` | Required. The route's stable identifier and telemetry alias. |
| `ProviderName` | `string?` | The provider that serves the route (for example, `"openai"`). |
| `ModelId` | `string?` | The concrete, billable model ID forwarded to the client. |
| `ReasoningEffort` | `ReasoningEffort?` | A reasoning-effort hint forwarded with the request. |
| `MaxInputTokens` | `int?` | A context-window hint. |
| `InputTokenCostPerMillion` | `decimal?` | Input cost per million tokens. |
| `OutputTokenCostPerMillion` | `decimal?` | Output cost per million tokens. |
| `TypicalLatency` | `TimeSpan?` | A latency hint. |
| `SourceUri` | `Uri?` | Provenance: where the metadata came from. |
| `UpdatedAt` | `DateTimeOffset?` | Provenance: when the metadata was last refreshed. |
| `AdditionalProperties` | `AdditionalPropertiesDictionary?` | An open bag for anything not first-class (for example, capability tokens or a quality score). |
| `Client` | `IChatClient?` | The client that serves the route. Required to dispatch. |

The first-class surface is intentionally objective and small: provider and model identity, cost,
context window, latency, and provenance. Subjective or custom dimensions — a quality score, benchmark
results, region, or any provider-specific signal — go in `AdditionalProperties` under app-specific keys
for your policy to read:

```csharp
new ChatRoute(
    name: "openai:gpt-5.3",
    providerName: "openai",
    modelId: "gpt-5.3",
    additionalProperties: new AdditionalPropertiesDictionary
    {
        ["capabilities"] = new[] { "reasoning", "function_calling" },
        ["quality"] = 0.95,
        ["region"] = "us-east",
    });
```

### Reuse metadata with `WithClient`

Build client-less `ChatRoute` metadata once (for example, a shared table of known models) and bind each
entry to a client where you wire up a router. `WithClient` returns a copy of the route with the same
metadata and the specified client, so the *catalog* of known models stays separate from the *wiring* of a
specific router:

```csharp
ChatRoute[] catalog =
[
    new ChatRoute("openai:gpt-5.3", providerName: "openai", modelId: "gpt-5.3",
        inputTokenCostPerMillion: 10, outputTokenCostPerMillion: 30),
    new ChatRoute("openai:gpt-4o-mini", providerName: "openai", modelId: "gpt-4o-mini",
        inputTokenCostPerMillion: 1, outputTokenCostPerMillion: 3),
];

// Bind entries to a router — for example the cheapest-first policy defined below:
IChatClient router = new CheapestRouteClient(
[
    catalog[0].WithClient(gpt53Client),
    catalog[1].WithClient(gpt4oMiniClient),
]);
```

Mapping an external catalog (LiteLLM, GitHub Models, provider feeds) into `ChatRoute` entries is left to
the caller, because catalog formats and provenance requirements differ. The
[cookbook](./routing-chat-client-cookbook.md) shows a worked LiteLLM loader.

## Write a policy: derive from `RoutingChatClient`

The [first-policy example](#get-started-your-first-policy) above is a `RoutingChatClient` subclass. Every
policy follows the same shape — override `SelectNextRouteAsync`:

```csharp
protected abstract ValueTask<ChatRoute?> SelectNextRouteAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IReadOnlyList<ChatRoute> routes,
    IReadOnlyList<ChatRoute> attempted,
    Exception? lastException,
    CancellationToken cancellationToken);
```

| Parameter | Meaning |
|---|---|
| `messages` | The request messages. |
| `options` | The request options (including any caller-pinned `ModelId`). |
| `routes` | All registered routes, in registration order. |
| `attempted` | The routes already tried this request, in attempt order. Empty on the first call. |
| `lastException` | The failure from the most recent attempt. `null` on the first call. |
| `cancellationToken` | The request's cancellation token. |

Return the route to try next, or `null` to stop. Remember the rules from
[How routing works](#how-routing-works): the returned route must be one of the `routes` instances
(by reference); returning a route already in `attempted` stops the loop; `null` on the first call throws,
`null` after a failure rethrows `lastException`.

A minimal cheapest-first policy that reads the advisory metadata:

```csharp
public sealed class CheapestRouteClient : RoutingChatClient
{
    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes) : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        // Cheapest route not yet attempted; null when the ranked chain is exhausted.
        ChatRoute? next = routes
            .Except(attempted)
            .OrderBy(r => r.InputTokenCostPerMillion ?? decimal.MaxValue)
            .FirstOrDefault();

        return new ValueTask<ChatRoute?>(next);
    }
}
```

Because selection, filtering, and failover are the same method, richer policies fall out naturally:
classify the request on the first call and pick one route; on later calls interpret `lastException` to
prune a whole provider or return the cheapest remaining route; skip an unhealthy route by never
returning it. The [cookbook](./routing-chat-client-cookbook.md) works through these archetypes.

The base class exposes the registered routes to subclasses through the `protected Routes` property, so a
policy that keeps its own per-route state (health, cooldowns) can enumerate them at construction.

For complete, copy-ready policies — difficulty tiering, embedding similarity, sticky sessions, cooldown,
circuit breaker, ordered failover, and cheapest-that-fits — see the
[cookbook](./routing-chat-client-cookbook.md) and its [sample subclasses](./samples/routing/).

## Observe routing decisions

The router records the outcome of each request in two complementary ways: response properties and trace
events. Both are read-only observations; routing internals are never written into the forwarded
`ChatOptions`. Only the chosen route's `ModelId` is forwarded to its client, and only when the caller
did not pin one.

### Response properties

The chosen route is stamped on the response (and on the first streaming update) under these keys:

| Constant | Property key | Description |
|---|---|---|
| `RoutingChatClient.SelectedRouteNameKey` | `routing.selected_route` | The route `Name` that answered — a router-local alias. |
| `RoutingChatClient.SelectedModelIdKey` | `routing.selected_model_id` | The concrete, billable model. Use this for cost and usage attribution. |
| `RoutingChatClient.SelectedProviderNameKey` | `routing.selected_provider` | The provider that answered. |
| `RoutingChatClient.SelectedPathKey` | `routing.selected_path` | The full winning path through any nested routers (for example, `"eu/gpt-4o-mini"`). |

Under nesting, identity is stamped first-writer-wins, so it stays leaf-truthful: the innermost router
records the concrete route that produced the tokens, and an outer router does not overwrite it. The path
accumulates instead — each router prepends the route it selected as the response unwinds.

```csharp
ChatResponse response = await router.GetResponseAsync(messages);

response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedModelIdKey, out object? modelId);
response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedPathKey, out object? path);
```

### Trace events

Per request, the router starts a `routing.route` [`Activity`](https://learn.microsoft.com/dotnet/api/system.diagnostics.activity)
from an `ActivitySource` named `RoutingChatClient.ActivitySourceName` (`"Microsoft.Extensions.AI.Routing"`).
Nesting composes: each router opens its own span, so nested routers form a span tree. When no listener
subscribes to the source, `StartActivity` returns `null` at effectively zero cost and the events below
fall back to the ambient `Activity.Current`.

Onto that activity the router adds two kinds of `ActivityEvent`. Both are no-ops unless the activity is
being recorded (`Activity.Current is { IsAllDataRequested: true }`), so an unsampled request pays
nothing.

- **`RoutingChatClient.DecisionEventName` (`routing.decision`)** — one per request, describing the route
  selected first. It carries the `routing.selected_route` tag.
- **`RoutingChatClient.AttemptEventName` (`routing.attempt`)** — one per route actually attempted, in
  order, capturing the fallback timeline. Tags: `routing.attempt.ordinal`, `routing.attempt.route`,
  `routing.attempt.outcome` (`success`, `fallback`, or `error`), `routing.attempt.duration_ms`,
  `routing.attempt.model_id` and `routing.attempt.provider` (when set), and — on failure —
  `routing.attempt.error_type`.

The decision event answers *why this route*; the attempt events answer *what the router did*. To capture
them, subscribe to the source — for example, add `"Microsoft.Extensions.AI.Routing"` to your
OpenTelemetry tracer provider, or attach an `ActivityListener` — or run the router within an outer
sampled activity (such as the span from `UseOpenTelemetry`). The event names are public so consumers can
filter by name; the tag keys are a telemetry detail, so observe them through tracing rather than binding
to them in code.

## Related content

- [RoutingChatClient cookbook](./routing-chat-client-cookbook.md)
- [Sample routing policies](./samples/routing/)
- [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
