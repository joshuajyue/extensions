# Route chat requests with `RoutingChatClient`

`RoutingChatClient` is an [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
that owns several candidate routes and forwards each request to one of them. It is an **abstract base
class**: it owns the routing *mechanism* — holding the candidates, dispatching, and walking fallbacks —
and defers the routing *policy* to a single method you override.

There are two types to learn:

- **`ChatRoute`** — a sealed invocation target with a name, a required `IChatClient`, and optional request
  defaults.
- **`RoutingChatClient`** (abstract) — the mechanism. Override one method,
  [`SelectRouteAsync`](#write-a-policy-derive-from-routingchatclient), to decide which route to try
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

All routing types live in the `Microsoft.Extensions.AI` namespace and ship in the
`Microsoft.Extensions.AI` package, so a single `using Microsoft.Extensions.AI;` covers them.

| Package | Contents |
|---|---|
| `Microsoft.Extensions.AI` | `ChatRoute` and the abstract `RoutingChatClient`. |

## How routing works

Routing is built on **one seam**. Instead of separate concepts for selection, filtering, and fallback,
a router answers a single question, repeatedly, until it has a response:

> Given the request and everything attempted so far (and why the last attempt failed), which route do I
> try next — or `null` to stop?

That question is `RoutingChatClient.SelectRouteAsync`. The base class calls it in a loop:

1. **Select.** Call `SelectRouteAsync` with an empty `attempted` list and a `null` `lastException`.
   The returned route is dispatched. Returning `null` here throws `InvalidOperationException` (a router
   with nothing to route to).
2. **Dispatch.** If the selected route supplies a `ModelId` or `ReasoningEffort` that the caller omitted,
   clone the request options (or create them) and fill those defaults. Forward the request to the route's
   `Client`. If it succeeds (or, when streaming, produces its first update), the response is returned
   unchanged.
3. **Fall back.** If the dispatch fails *before* producing output, call `SelectRouteAsync` again —
   now with the failed route appended to `attempted` and the thrown exception as `lastException`. The
   returned route is dispatched next. Returning `null` here **rethrows the last exception**.

The three classic routing behaviors are all expressions of this one method:

- **Selection** is the first call (`attempted` is empty).
- **Failover** is a later call (`lastException` is set; `attempted[^1]` is the route that just failed).
- **Filtering** is simply *not returning* a route you don't want to try.

The policy may return any usable route and may retry one already present in `attempted`; it is responsible
for eventually returning `null` if attempts keep failing. The router owns and disposes only the routes
registered with its constructor, so the policy owns the lifetime of any other route it returns. Cancellation
is never treated as a failure and never triggers fallback. When streaming, fallback applies only until the
first update is produced — once a token is on the wire the router never re-routes.

Because `RoutingChatClient` is itself an `IChatClient`, and each route is bound to an `IChatClient`, a
routing pipeline forms a tree: a route's `Client` can be another `RoutingChatClient`. Route coarsely at
the top (for example, by region or tenant) and finely below (for example, by cost).

## Get started: your first policy

A policy is a `RoutingChatClient` subclass that overrides `SelectRouteAsync`. Here is a minimal one
that routes short prompts to a small model and everything else to a large one, then falls back to any
remaining route if the first choice fails — three routing behaviors (selection, filtering, failover) in
one method:

```csharp
using Microsoft.Extensions.AI;

public sealed class BySizeRouter : RoutingChatClient
{
    public BySizeRouter(IReadOnlyList<ChatRoute> routes) : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
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

Wire it up by constructing each route with an `IChatClient`:

```csharp
IChatClient router = new BySizeRouter(
[
    new ChatRoute("small", openaiMini, modelId: "gpt-4o-mini"),
    new ChatRoute("large", openai,     modelId: "gpt-4o"),
]);

ChatResponse response = await router.GetResponseAsync("Hello!"); // short → "small"
```

That is the whole pattern. Real policies differ only in the body of `SelectRouteAsync`: a difficulty
classifier, an embedding router, a cooldown gate, or plain ordered failover. The
[cookbook](./routing-chat-client-cookbook.md) has each as a complete, copy-ready sample; the rest of this
article covers the pieces the example above uses.

## Configure routes

`ChatRoute` is the sealed, minimal invocation target a router chooses between:

```csharp
public ChatRoute(
    string name,
    IChatClient client,
    string? modelId = null,
    ReasoningEffort? reasoningEffort = null,
    AdditionalPropertiesDictionary? additionalProperties = null);
```

It exposes five get-only properties:

| Member | Type | Description |
|---|---|---|
| `Name` | `string` | Required. The route's stable identifier and routing alias. |
| `Client` | `IChatClient` | Required and non-null. The client invoked when the route is selected. |
| `ModelId` | `string?` | A model-id request default applied at dispatch. |
| `ReasoningEffort` | `ReasoningEffort?` | A reasoning-effort request default applied at dispatch. |
| `AdditionalProperties` | `AdditionalPropertiesDictionary?` | Application-owned policy metadata; RCC never interprets it. |

When a selected route contributes a missing default, `RoutingChatClient` clones the caller's `ChatOptions`
(or creates options when none were supplied) before applying it. Route `ModelId` fills
`ChatOptions.ModelId`, and route `ReasoningEffort` fills `ChatOptions.Reasoning.Effort`. Caller values
always win, and the caller's options instance is never mutated.

This lets multiple logical routes share one physical client. Each route can carry different invocation
defaults, and the router applies the selected route's values when it calls that shared client:

```csharp
IChatClient sharedOpenAIClient = CreateOpenAIClient();

ChatRoute[] routes =
[
    new ChatRoute("fast", sharedOpenAIClient, modelId: "gpt-4o-mini"),
    new ChatRoute(
        "deep",
        sharedOpenAIClient,
        modelId: "o3",
        reasoningEffort: ReasoningEffort.High),
];
```

If the policy selects `fast`, the shared client receives `gpt-4o-mini`; if it selects `deep` (including as
a fallback), the same client receives `o3` and high reasoning effort.

### Keep catalog and policy metadata application-owned

Cost, capability, context-window, latency, provenance, provider, and other catalog data belong to the
application or routing policy. Store strongly typed values in `AdditionalProperties` so reusable selectors
can inspect them without RCC assigning framework semantics:

```csharp
public sealed record ModelCatalogEntry(
    string RouteName,
    string Provider,
    string ModelId,
    ReasoningEffort? DefaultReasoningEffort,
    int ContextWindowTokens,
    decimal InputCostPerMillionTokens,
    decimal OutputCostPerMillionTokens,
    Uri MetadataSource,
    DateTimeOffset MetadataRefreshedAt)
{
    public const string MetadataKey = "catalog";

    public ChatRoute Bind(IChatClient client) =>
        new(
            RouteName,
            client,
            ModelId,
            DefaultReasoningEffort,
            new AdditionalPropertiesDictionary { [MetadataKey] = this });
}

ModelCatalogEntry[] catalog =
[
    new(
        "openai:gpt-5.3", "openai", "gpt-5.3", ReasoningEffort.High,
        128_000, 10, 30, new Uri("https://example.com/model-catalog"), DateTimeOffset.UtcNow),
    new(
        "openai:gpt-4o-mini", "openai", "gpt-4o-mini", null,
        128_000, 1, 3, new Uri("https://example.com/model-catalog"), DateTimeOffset.UtcNow),
];

// Composition creates required, client-bound invocation targets.
ChatRoute[] runtimeRoutes = catalog
    .Select(entry => entry.Bind(sharedOpenAIClient))
    .ToArray();

// A selector can recover the typed metadata directly from the route.
ModelCatalogEntry metadata =
    runtimeRoutes[0].AdditionalProperties![ModelCatalogEntry.MetadataKey] as ModelCatalogEntry
    ?? throw new InvalidOperationException();
```

Mapping external catalogs (LiteLLM, GitHub Models, provider feeds) into application-owned records is left
to the caller because formats and policy requirements differ. The
[cookbook](./routing-chat-client-cookbook.md) shows a worked loader.

## Write a policy: derive from `RoutingChatClient`

The [first-policy example](#get-started-your-first-policy) above is a `RoutingChatClient` subclass. Every
policy follows the same shape — override `SelectRouteAsync`:

```csharp
protected abstract ValueTask<ChatRoute?> SelectRouteAsync(
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
| `options` | The caller's request options (including any pinned `ModelId`); route defaults are applied after selection. |
| `routes` | All registered routes, in registration order. |
| `attempted` | The routes already tried this request, in attempt order. Empty on the first call. |
| `lastException` | The failure from the most recent attempt. `null` on the first call. |
| `cancellationToken` | The request's cancellation token. |

Return the route to try next, or `null` to stop. The policy may retry an attempted route or return a route
outside `routes`; it owns those decisions and must eventually return `null` if attempts keep failing.
`null` on the first call throws; `null` after a failure rethrows `lastException`.

A minimal cheapest-first policy reads typed, application-owned cost metadata from each route:

```csharp
public sealed record RouteCostConfiguration(
    int? ContextWindowTokens,
    decimal? InputCostPerMillionTokens);

public sealed class CheapestRouteClient : RoutingChatClient
{
    public const string ConfigurationKey = "cost";

    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes)
        : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectRouteAsync(
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
            .Select(route => (
                Route: route,
                Cost: route.AdditionalProperties?.TryGetValue(
                    ConfigurationKey,
                    out RouteCostConfiguration? cost) == true
                        ? cost
                        : null))
            .OrderBy(candidate =>
                candidate.Cost?.InputCostPerMillionTokens ?? decimal.MaxValue)
            .Select(candidate => candidate.Route)
            .FirstOrDefault();

        return new ValueTask<ChatRoute?>(next);
    }
}
```

Because selection, filtering, and failover are the same method, richer policies fall out naturally:
classify the request on the first call and pick one route; on later calls interpret `lastException` to
prune routes using an app-owned provider map or return the cheapest remaining route; skip an unhealthy
route by never returning it. The [cookbook](./routing-chat-client-cookbook.md) works through these
archetypes.

The base class exposes the registered routes to subclasses through the `protected Routes` property, so a
policy that keeps its own per-route state (health, cooldowns) can enumerate them at construction.

For complete, copy-ready policies — difficulty tiering, embedding similarity, sticky sessions, cooldown,
circuit breaker, ordered failover, and cheapest-that-fits — see the
[cookbook](./routing-chat-client-cookbook.md) and its [sample subclasses](./samples/routing/).

## OpenTelemetry

`RoutingChatClient` participates in the standard MEAI telemetry pipeline like any other `IChatClient`.
Wrap the router with `UseOpenTelemetry` to trace the overall routed request:

```csharp
IChatClient instrumentedRouter = router
    .AsBuilder()
    .UseOpenTelemetry()
    .Build();
```

To trace each attempted model independently, wrap each client before constructing its route.
Those invocation spans naturally become children of the overall router span and capture failed primary
attempts as well as the eventual successful fallback. RCC itself does not define a separate telemetry
source or custom event schema, and it returns each selected client's response unchanged.

## Related content

- [RoutingChatClient cookbook](./routing-chat-client-cookbook.md)
- [Sample routing policies](./samples/routing/)
- [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
