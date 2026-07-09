# RoutingChatClient: a cookbook of use cases

This cookbook shows how to solve real routing problems with `RoutingChatClient`. For the reference
description, see [routing-chat-client.md](./routing-chat-client.md).

> [!IMPORTANT]
> The routing types are experimental (`[Experimental("MEAI001")]`). Opt in deliberately, for example
> with `#pragma warning disable MEAI001`.

## Where the types live

Everything is in the `Microsoft.Extensions.AI` namespace.

| Package | Types |
|---|---|
| `Microsoft.Extensions.AI.Abstractions` | `ChatRoute` |
| `Microsoft.Extensions.AI` | `RoutingChatClient` (abstract) |

## The one seam

Routing has a single extension point: `RoutingChatClient.SelectNextRouteAsync`. You **derive from
`RoutingChatClient`** and override that one method; the base class owns everything else — dispatch,
fallback, telemetry, response stamping, and disposal. The base calls your method in a loop and asks the
same question each time — *given the request and what has been attempted (and why the last attempt
failed), which route next, or `null` to stop?*

- **Selection** = the first call (`attempted` empty, `lastException` null).
- **Failover** = a later call (`lastException` set, `attempted[^1]` is the route that failed).
- **Filtering** = simply not returning a route.

That is the whole model: *selection, filtering, and failover are the same method.* A difficulty
classifier, an embedding router, a cooldown gate, and ordered failover are all just different bodies for
`SelectNextRouteAsync`.

Rules the base class enforces:

- The returned route must be one of the registered instances, **matched by reference** — a reconstructed
  `ChatRoute` with identical metadata makes the router throw. Resolve by name against `routes`:
  ```csharp
  ChatRoute pick = routes.First(r =>
      string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase));
  ```
- A route already in `attempted` stops the loop (so routing always terminates).
- `null` on the first call throws; `null` after a failure rethrows the last exception.
- Cancellation never triggers fallback.
- When streaming, fallback stops once the first token is on the wire.

## The samples

Each recipe below links to a complete, copy-ready policy in
[samples/routing/](./samples/routing/). Grab the file, drop it in your project, and adapt it.

| Policy | Sample | Recipe |
|---|---|---|
| Route by difficulty (rule-based, no model call) | [ComplexityRoutingClient.cs](./samples/routing/ComplexityRoutingClient.cs) | [Route by difficulty](#route-by-difficulty) |
| Route by meaning (embedding similarity) | [SemanticRoutingClient.cs](./samples/routing/SemanticRoutingClient.cs) | [Route by meaning](#route-by-meaning) |
| Require a capability (tools / vision / JSON) | [CapabilityGatingClient.cs](./samples/routing/CapabilityGatingClient.cs) | [Require a capability](#require-a-capability) |
| Sticky conversations | [StickyRoutingClient.cs](./samples/routing/StickyRoutingClient.cs) | [Sticky sessions](#sticky-sessions) |
| Skip rate-limited routes | [CooldownRoutingClient.cs](./samples/routing/CooldownRoutingClient.cs) | [Cooldown](#cooldown) |
| Trip a route after repeated failures | [CircuitBreakerRoutingClient.cs](./samples/routing/CircuitBreakerRoutingClient.cs) | [Circuit breaker](#circuit-breaker) |
| Try routes in order until one works | [OrderedFailoverClient.cs](./samples/routing/OrderedFailoverClient.cs) | [Ordered failover](#ordered-failover) |
| Cheapest route that fits | [CheapestRouteClient.cs](./samples/routing/CheapestRouteClient.cs) | [Cheapest that fits](#cheapest-that-fits) |

---

## Route by difficulty

Classify each request into a difficulty tier with fast, deterministic keyword/pattern scoring (no extra
model call), then send it to the model you mapped to that tier — cheap models for small talk, a reasoning
model for hard problems. On the first call the policy classifies and picks one route; on later calls it
falls back through the rest.

Full sample: [ComplexityRoutingClient.cs](./samples/routing/ComplexityRoutingClient.cs).

```csharp
IChatClient router = new ComplexityRoutingClient(
    routes:
    [
        new ChatRoute("small",     modelId: "gpt-4o-mini",   client: openaiMini),
        new ChatRoute("large",     modelId: "gpt-4o",        client: openai),
        new ChatRoute("reasoning", modelId: "o3",            client: openaiReasoning),
    ],
    routeByTier: new Dictionary<ComplexityTier, string>
    {
        [ComplexityTier.Simple]    = "small",
        [ComplexityTier.Medium]    = "small",
        [ComplexityTier.Complex]   = "large",
        [ComplexityTier.Reasoning] = "reasoning",
    },
    defaultRoute: "large");

var quick = await router.GetResponseAsync("hi there");                        // → small
var hard  = await router.GetResponseAsync("Refactor this service step by step to remove the lock."); // → reasoning
```

The classifier is a pure function you can call directly (`ComplexityRoutingClient.Classify(messages)`),
so it is easy to unit-test the tiering independently of any client.

---

## Route by meaning

Describe each route with a handful of representative "utterances", then route each request to the route
whose utterances are semantically closest to the user's message. This is the semantic-router approach
(what LiteLLM's "auto router" delegates to): embed the query, cosine-compare it against every route's
utterances, and pick the best route that clears a similarity threshold — otherwise a default route.

Full sample: [SemanticRoutingClient.cs](./samples/routing/SemanticRoutingClient.cs).

```csharp
IEmbeddingGenerator<string, Embedding<float>> embeddings = CreateEmbeddingGenerator();

IChatClient router = new SemanticRoutingClient(
    routes:
    [
        new ChatRoute("code",    client: codeModel),
        new ChatRoute("support", client: supportModel),
        new ChatRoute("general", client: generalModel),
    ],
    embeddings,
    routeProfiles: new Dictionary<string, IReadOnlyList<string>>
    {
        ["code"]    = ["write a function", "fix this stack trace", "refactor this class"],
        ["support"] = ["reset my password", "cancel my subscription", "I was double charged"],
        ["general"] = ["what's the weather", "tell me a joke", "who won the game"],
    },
    defaultRoute: "general");

var r = await router.GetResponseAsync("my deployment throws a NullReferenceException"); // → code
```

Profiles are embedded once and cached. Wrap the injected generator with caching to also amortize the
per-request query embedding.

---

## Require a capability

The difficulty and meaning policies express a *preference*; capability gating expresses a *requirement*. A
request that carries a tool, an image, or a JSON-schema response format must never reach a route that can't
serve it. This policy reads what each request actually needs and returns the first unattempted route that
advertises all of it — so it doubles as a correctness filter and a capability-aware fallback chain.

Each route advertises what it supports through a `ModelCapabilities` flags value stored in
`ChatRoute.AdditionalProperties` (the "capability tokens an application's own candidate filter reads" that
`ChatRoute` is designed to carry). If no capable route remains, the base class throws or rethrows rather
than silently downgrading to an incapable model.

Full sample: [CapabilityGatingClient.cs](./samples/routing/CapabilityGatingClient.cs).

```csharp
IChatClient router = new CapabilityGatingClient(
[
    new ChatRoute("mini",  modelId: "gpt-4o-mini", client: openaiMini,
        additionalProperties: new() { ["capabilities"] = ModelCapabilities.ToolCalling }),
    new ChatRoute("omni",  modelId: "gpt-4o",      client: openai,
        additionalProperties: new() { ["capabilities"] =
            ModelCapabilities.ToolCalling | ModelCapabilities.Vision | ModelCapabilities.StructuredOutput }),
]);

// A plain chat can be served by either route → "mini".
var text = await router.GetResponseAsync("summarize this thread");

// An image-bearing request requires Vision → only "omni" qualifies.
var vision = await router.GetResponseAsync(
    new ChatMessage(ChatRole.User, [new TextContent("what's in this picture?"), new UriContent(imageUri, "image/png")]));
```

The requirement derivation is a pure function you can call directly
(`CapabilityGatingClient.RequiredCapabilities(messages, options)`), so it is easy to unit-test what a given
request demands independently of any client.

---

## Sticky sessions

Keep a multi-turn conversation on the model that started it, without hard-coding which model that is.
Stickiness is an application concern, so the policy holds no state: it calls back into your app for the
route names a request is pinned to, and defers to an inner policy when nothing is pinned. Key pins on an
**app-owned stable session id** — not `ChatOptions.ConversationId`, which some providers rotate per
message.

Full sample: [StickyRoutingClient.cs](./samples/routing/StickyRoutingClient.cs).

```csharp
// Your app decides what a request is pinned to (e.g. from a session store).
ConcurrentDictionary<string, string> pinnedRouteBySession = new();

IChatClient router = new StickyRoutingClient(
    routes,
    getPins: (messages, options) =>
        options?.ConversationId is { } sessionKey &&           // your stable key, not the provider's
        pinnedRouteBySession.TryGetValue(sessionKey, out string? route)
            ? [route]
            : null,
    inner: (messages, options, routes, attempted, ex, ct) =>   // first turn: choose + remember
        new(routes.Except(attempted).FirstOrDefault()));
```

A pin that no longer resolves to a registered route is skipped, so a stale pin can never dead-end a
request; it simply re-attaches once the route exists again.

---

## Resilience

### Ordered failover

The simplest useful policy: try routes in order until one succeeds, honoring an explicitly requested model
first. It's a dozen lines — the canonical starting point you copy and specialize.

Full sample: [OrderedFailoverClient.cs](./samples/routing/OrderedFailoverClient.cs).

```csharp
IChatClient router = new OrderedFailoverClient(
[
    new ChatRoute("claude-sonnet", providerName: "anthropic", modelId: "claude-sonnet-4", client: anthropic),
    new ChatRoute("gemini-flash",  providerName: "google",    modelId: "gemini-2.5-flash", client: gemini),
    new ChatRoute("gpt-mini",      providerName: "openai",     modelId: "gpt-4o-mini",      client: openai),
]);

// No pin → first route; if it fails before output, fall back in order.
var a = await router.GetResponseAsync("Summarize this PDF.");

// Pin a route by model id (or route name).
var b = await router.GetResponseAsync("Summarize this PDF.", new ChatOptions { ModelId = "gemini-2.5-flash" });
```

Because each candidate is itself an `IChatClient`, you can give any one of them its own middleware (for
example wrap `openai` in `.AsBuilder().UseFunctionInvocation().Build()` before binding it), and wrap the
whole router the same way. In DI:

```csharp
services.AddChatClient(sp => new OrderedFailoverClient(
[
    new ChatRoute("primary", client: sp.GetRequiredKeyedService<IChatClient>("primary")),
    new ChatRoute("backup",  client: sp.GetRequiredKeyedService<IChatClient>("backup")),
]));
```

### Cooldown

Put a route on a timed cooldown when it rate-limits, and skip it while it cools — filtering *is* just not
returning a route. On failure the policy reads a `Retry-After` header off the exception and records a
cool-until time for the route that failed. Because the router is a DI singleton, that state persists
across requests; cooldowns self-expire, so no success signal is needed to reinstate a route.

Full sample: [CooldownRoutingClient.cs](./samples/routing/CooldownRoutingClient.cs).

```csharp
IChatClient router = new CooldownRoutingClient(
[
    new ChatRoute("primary", client: primary),
    new ChatRoute("backup",  client: backup),
]);
```

### Circuit breaker

Stop hammering a route that keeps failing: after a threshold of consecutive failures its circuit opens and
the route is skipped for a reset window, then allowed a single half-open trial. The routing seam only
observes failures, so this breaker resets on a timer. To close the instant a route recovers, wrap each
route's client (or the whole router) in a `DelegatingChatClient` that clears the route's failure count on a
successful response.

Full sample: [CircuitBreakerRoutingClient.cs](./samples/routing/CircuitBreakerRoutingClient.cs).

### Prune a provider on an auth error

Interpret `lastException` and drop whole providers. On a 401 there's no point trying other routes that
share the dead credential — just don't return them:

```csharp
if (lastException is ClientResultException { Status: 401 or 403 })
{
    ChatRoute dead = attempted[^1];
    return new(routes
        .Except(attempted)
        .Where(r => !string.Equals(r.ProviderName, dead.ProviderName, StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault());
}
```

---

## Cheapest that fits

Read the advisory `ChatRoute` cost and context-window hints and return the cheapest route whose window
admits the prompt. Because selection and fallback are the same method, this forms a cost-ordered fallback
chain automatically.

Full sample: [CheapestRouteClient.cs](./samples/routing/CheapestRouteClient.cs).

```csharp
ChatRoute? next = routes
    .Except(attempted)
    .Where(r => r.MaxInputTokens is null || r.MaxInputTokens >= approxTokens) // context-window filter
    .OrderBy(r => r.InputTokenCostPerMillion ?? decimal.MaxValue)             // cheapest first
    .FirstOrDefault();
```

---

## Reuse model metadata

Build client-less `ChatRoute` metadata once — for example by mapping an external catalog (LiteLLM, GitHub
Models, provider feeds) — then bind each entry to a client where you wire up a router with `WithClient`.
Catalog parsing is the caller's job; here is a compact LiteLLM-style loader.

```csharp
using System.Text.Json;

static IReadOnlyDictionary<string, ChatRoute> LoadLiteLlm(JsonDocument doc)
{
    var routes = new Dictionary<string, ChatRoute>(StringComparer.OrdinalIgnoreCase);
    foreach (JsonProperty model in doc.RootElement.EnumerateObject())
    {
        if (model.Name == "sample_spec")
        {
            continue;
        }

        JsonElement v = model.Value;
        routes[model.Name] = new ChatRoute(
            name: model.Name,
            providerName: v.TryGetProperty("litellm_provider", out var p) ? p.GetString() : null,
            modelId: model.Name,
            maxInputTokens: v.TryGetProperty("max_input_tokens", out var mit) ? mit.GetInt32() : null,
            inputTokenCostPerMillion: v.TryGetProperty("input_cost_per_token", out var ic)
                ? ic.GetDecimal() * 1_000_000 : null,
            outputTokenCostPerMillion: v.TryGetProperty("output_cost_per_token", out var oc)
                ? oc.GetDecimal() * 1_000_000 : null,
            sourceUri: new Uri("https://github.com/BerriAI/litellm"));
    }

    return routes;
}

// Bind the entries you want to a router:
IReadOnlyDictionary<string, ChatRoute> catalog = LoadLiteLlm(JsonDocument.Parse(json));

IChatClient router = new ComplexityRoutingClient(
    routes:
    [
        catalog["gpt-4o-mini"].WithClient(openaiMini),
        catalog["gpt-4o"].WithClient(openai),
    ],
    routeByTier: new Dictionary<ComplexityTier, string>
    {
        [ComplexityTier.Simple] = "gpt-4o-mini",
        [ComplexityTier.Complex] = "gpt-4o",
    });
```

---

## Composition

### Nested routers (a routing tree)

A route's `Client` can be another `RoutingChatClient`. Route coarsely at the top, finely below:

```csharp
IChatClient usRouter = new OrderedFailoverClient(usRoutes);
IChatClient euRouter = new OrderedFailoverClient(euRoutes);

IChatClient root = new RegionRouterClient(   // your RoutingChatClient subclass
[
    new ChatRoute("us", client: usRouter),
    new ChatRoute("eu", client: euRouter),
]);
```

Telemetry composes cleanly: each router opens its own `routing.route` span, and the winning path is
stamped on the response under `RoutingChatClient.SelectedPathKey` (for example, `"eu/gpt-4o-mini"`).

### Reading the routing decision off the response

The chosen route is stamped on the **response** (never mutated into the request):

```csharp
var response = await router.GetResponseAsync(messages);

response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedRouteNameKey, out object? route);
response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedModelIdKey, out object? modelId);
response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedPathKey, out object? path);
// route   → the router-local alias that answered
// modelId → the concrete billed model (use this for cost/usage attribution)
// path    → the full winning path through any nested routers
```

For the full trace-event schema (`routing.decision`, `routing.attempt`), see
[routing-chat-client.md](./routing-chat-client.md#trace-events).

---

## Cheat sheet

| I want to… | Reach for |
|---|---|
| Route by **difficulty** | [ComplexityRoutingClient.cs](./samples/routing/ComplexityRoutingClient.cs) |
| Route by **meaning** | [SemanticRoutingClient.cs](./samples/routing/SemanticRoutingClient.cs) |
| Require a **capability** (tools/vision/JSON) | [CapabilityGatingClient.cs](./samples/routing/CapabilityGatingClient.cs) |
| Keep a conversation **sticky** to one model | [StickyRoutingClient.cs](./samples/routing/StickyRoutingClient.cs) |
| **Skip** a rate-limited route until it cools | [CooldownRoutingClient.cs](./samples/routing/CooldownRoutingClient.cs) |
| **Trip** a route after repeated failures | [CircuitBreakerRoutingClient.cs](./samples/routing/CircuitBreakerRoutingClient.cs) |
| Try routes **in order until one works** | [OrderedFailoverClient.cs](./samples/routing/OrderedFailoverClient.cs) |
| **Cheapest that fits** | [CheapestRouteClient.cs](./samples/routing/CheapestRouteClient.cs) |
| **Pin** a request to a specific model/route | `ChatOptions.ModelId` (read in your `SelectNextRouteAsync`) |
| **Prune a provider** on an auth error | interpret `lastException`, don't return that provider's routes |
| Route across **different providers/clients** | routes bound via `client:` / `WithClient` |
| **Nested** region/tenant → model routing | a route whose `Client` is another `RoutingChatClient` |
| Reuse **model metadata** | client-less `ChatRoute` entries (+ your JSON loader) bound with `WithClient` |
