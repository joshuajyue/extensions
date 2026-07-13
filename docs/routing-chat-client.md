# Route chat requests with `RoutingChatClient`

`RoutingChatClient` is an [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
that owns several candidate routes and forwards each request to one of them. It is an **abstract base
class**: it owns the routing *mechanism* — holding the candidates, dispatching, and walking fallbacks —
and defers the routing *policy* to a single method you override.

There is only one type to learn:

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

That question is `RoutingChatClient.SelectRouteAsync`. The base class calls it in a loop:

1. **Select.** Call `SelectRouteAsync` with an empty `attempted` list and a `null` `lastException`.
   The returned route is dispatched. Returning `null` here throws `InvalidOperationException` (a router
   with nothing to route to).
2. **Dispatch.** Forward the request to the route's `Client`. If it succeeds (or, when streaming,
   produces its first update), the response is returned unchanged.
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

Wire it up by binding each route to an `IChatClient`:

```csharp
IChatClient router = new BySizeRouter(
[
    new ChatRoute("small", modelId: "gpt-4o-mini", client: openaiMini),
    new ChatRoute("large", modelId: "gpt-4o",      client: openai),
]);

ChatResponse response = await router.GetResponseAsync("Hello!"); // short → "small"
```

That is the whole pattern. Real policies differ only in the body of `SelectRouteAsync`: a difficulty
classifier, an embedding router, a cooldown gate, or plain ordered failover. The
[cookbook](./routing-chat-client-cookbook.md) has each as a complete, copy-ready sample; the rest of this
article covers the pieces the example above uses.

A `ChatRoute` is the unit a router chooses between. It carries a required `Name` (a stable, router-local
alias) and optional, advisory metadata. The mechanism reads only `Client` to dispatch; your policy decides
how to use the metadata.

| Member | Type | Description |
|---|---|---|
| `Name` | `string` | Required. The route's stable identifier and routing alias. |
| `ProviderName` | `string?` | The provider that serves the route (for example, `"openai"`). |
| `ModelId` | `string?` | An advisory provider-specific model identifier. |
| `ReasoningEffort` | `ReasoningEffort?` | An advisory reasoning-effort hint. |
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
| `options` | The request options (including any caller-pinned `ModelId`). |
| `routes` | All registered routes, in registration order. |
| `attempted` | The routes already tried this request, in attempt order. Empty on the first call. |
| `lastException` | The failure from the most recent attempt. `null` on the first call. |
| `cancellationToken` | The request's cancellation token. |

Return the route to try next, or `null` to stop. The policy may retry an attempted route or return a route
outside `routes`; it owns those decisions and must eventually return `null` if attempts keep failing.
`null` on the first call throws; `null` after a failure rethrows `lastException`.

A minimal cheapest-first policy that reads the advisory metadata:

```csharp
public sealed class CheapestRouteClient : RoutingChatClient
{
    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes) : base(routes) { }

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

## OpenTelemetry

`RoutingChatClient` participates in the standard MEAI telemetry pipeline like any other `IChatClient`.
Wrap the router with `UseOpenTelemetry` to trace the overall routed request:

```csharp
IChatClient instrumentedRouter = router
    .AsBuilder()
    .UseOpenTelemetry()
    .Build();
```

To trace each attempted model independently, wrap each route's client before binding it to the route.
Those invocation spans naturally become children of the overall router span and capture failed primary
attempts as well as the eventual successful fallback. RCC itself does not define a separate telemetry
source or custom event schema, and it returns each selected client's response unchanged.

## Related content

- [RoutingChatClient cookbook](./routing-chat-client-cookbook.md)
- [Sample routing policies](./samples/routing/)
- [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
