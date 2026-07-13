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
| `Microsoft.Extensions.AI` | Sealed `ChatRoute` invocation targets and the abstract `RoutingChatClient` |

## The one seam

Routing has a single extension point: `RoutingChatClient.SelectRouteAsync`. You **derive from
`RoutingChatClient`** and override that one method; the base class owns everything else — dispatch,
fallback, and disposal. The base calls your method in a loop and asks the
same question each time — *given the request and what has been attempted (and why the last attempt
failed), which route next, or `null` to stop?*

- **Selection** = the first call (`attempted` empty, `lastException` null).
- **Failover** = a later call (`lastException` set, `attempted[^1]` is the route that failed).
- **Filtering** = simply not returning a route.

That is the whole model: *selection, filtering, and failover are the same method.* A difficulty
classifier, an embedding router, a cooldown gate, and ordered failover are all just different bodies for
`SelectRouteAsync`.

Dispatch semantics:

- The policy may return any usable route and may retry a route already in `attempted`. It is responsible for
  eventually returning `null` if attempts keep failing.
- The router owns and disposes only the routes registered with its constructor. The policy owns the lifetime of
  any other route it returns.
- `null` on the first call throws; `null` after a failure rethrows the last exception.
- Cancellation never triggers fallback.
- When streaming, fallback stops once the first token is on the wire.

`ChatRoute` is deliberately minimal:

```csharp
public ChatRoute(
    string name,
    IChatClient client,
    string? modelId = null,
    ReasoningEffort? reasoningEffort = null,
    AdditionalPropertiesDictionary? additionalProperties = null);
```

Its properties are `Name`, `Client`, `ModelId`, `ReasoningEffort`, and `AdditionalProperties`. `Name` is the policy key,
`Client` is required and non-null, and `ModelId` and `ReasoningEffort` are request defaults. When a route
contributes a missing default, `RoutingChatClient` clones the caller's `ChatOptions` (creating options when
needed) before filling `ChatOptions.ModelId` or `ChatOptions.Reasoning.Effort`. Caller values win and the
original options are not mutated. `AdditionalProperties` carries application-owned policy metadata that RCC
never interprets; reusable selectors can store strongly typed cost, capability, provider, or catalog values there.

One client can therefore serve multiple logical routes. The selected route supplies different defaults to
the same client:

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

Selecting `fast` sends `gpt-4o-mini` to the shared client. Selecting `deep`, including during fallback,
sends `o3` with high reasoning effort to that same client.

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
        new ChatRoute("small",     openaiMini,      modelId: "gpt-4o-mini"),
        new ChatRoute("large",     openai,          modelId: "gpt-4o"),
        new ChatRoute(
            "reasoning",
            openaiReasoning,
            modelId: "o3",
            reasoningEffort: ReasoningEffort.High),
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
        new ChatRoute("code", codeModel),
        new ChatRoute("support", supportModel),
        new ChatRoute("general", generalModel),
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
is configured to support all of it — so it doubles as a correctness filter and a capability-aware fallback
chain.

Capabilities are policy-owned typed values stored in each route's `AdditionalProperties`. If no capable route
remains, the base class throws or rethrows rather than silently downgrading to an incapable model.

Full sample: [CapabilityGatingClient.cs](./samples/routing/CapabilityGatingClient.cs).

```csharp
IChatClient router = new CapabilityGatingClient(
[
    new ChatRoute(
        "mini",
        openaiMini,
        modelId: "gpt-4o-mini",
        additionalProperties: new()
        {
            [CapabilityGatingClient.CapabilitiesKey] = ModelCapabilities.ToolCalling,
        }),
    new ChatRoute(
        "omni",
        openai,
        modelId: "gpt-4o",
        additionalProperties: new()
        {
            [CapabilityGatingClient.CapabilitiesKey] =
                ModelCapabilities.ToolCalling |
                ModelCapabilities.Vision |
                ModelCapabilities.StructuredOutput,
        }),
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
    new ChatRoute("claude-sonnet", anthropic, modelId: "claude-sonnet-4"),
    new ChatRoute("gemini-flash",  gemini,    modelId: "gemini-2.5-flash"),
    new ChatRoute("gpt-mini",      openai,    modelId: "gpt-4o-mini"),
]);

// No pin → first route; if it fails before output, fall back in order.
var a = await router.GetResponseAsync("Summarize this PDF.");

// Pin a route by model id (or route name).
var b = await router.GetResponseAsync("Summarize this PDF.", new ChatOptions { ModelId = "gemini-2.5-flash" });
```

Because each route has its own `IChatClient`, you can give any one of them its own middleware (for example,
wrap `openai` in `.AsBuilder().UseFunctionInvocation().Build()` before constructing its route), and wrap
the whole router the same way. In DI:

```csharp
services.AddChatClient(sp => new OrderedFailoverClient(
[
    new ChatRoute("primary", sp.GetRequiredKeyedService<IChatClient>("primary")),
    new ChatRoute("backup",  sp.GetRequiredKeyedService<IChatClient>("backup")),
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
    new ChatRoute("primary", primary),
    new ChatRoute("backup", backup),
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
share the dead credential. Provider identity is application-owned policy configuration keyed by route
name:

```csharp
public sealed record RouteProviderConfiguration(string Provider);

IReadOnlyDictionary<string, RouteProviderConfiguration> providersByRouteName =
    new Dictionary<string, RouteProviderConfiguration>(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic-primary"] = new("anthropic"),
        ["anthropic-backup"] = new("anthropic"),
        ["openai-backup"] = new("openai"),
    };

if (lastException is ClientResultException { Status: 401 or 403 })
{
    ChatRoute dead = attempted[^1];
    string deadProvider = providersByRouteName[dead.Name].Provider;

    return new(routes
        .Except(attempted)
        .Where(route => !string.Equals(
            providersByRouteName[route.Name].Provider,
            deadProvider,
            StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault());
}
```

---

## Cheapest that fits

Keep cost and context-window data as a strongly typed value in each route's `AdditionalProperties`, then
return the cheapest route whose configured window admits the prompt. Because selection and fallback are
the same method, this forms a cost-ordered fallback chain automatically.

Full sample: [CheapestRouteClient.cs](./samples/routing/CheapestRouteClient.cs).

```csharp
IChatClient router = new CheapestRouteClient(
[
    new ChatRoute(
        "mini",
        openai,
        modelId: "gpt-4o-mini",
        additionalProperties: new()
        {
            [CheapestRouteClient.ConfigurationKey] =
                new RouteCostConfiguration(128_000, 0.15m),
        }),
    new ChatRoute(
        "large",
        openai,
        modelId: "gpt-4o",
        additionalProperties: new()
        {
            [CheapestRouteClient.ConfigurationKey] =
                new RouteCostConfiguration(128_000, 2.50m),
        }),
]);

ChatRoute? next = routes
    .Except(attempted)
    .Select(route => (
        Route: route,
        Configuration: route.AdditionalProperties![
            CheapestRouteClient.ConfigurationKey] as RouteCostConfiguration))
    .Where(candidate =>
        candidate.Configuration.ContextWindowTokens is null ||
        candidate.Configuration.ContextWindowTokens >= approxTokens)
    .OrderBy(candidate =>
        candidate.Configuration.InputCostPerMillionTokens ?? decimal.MaxValue)
    .Select(candidate => candidate.Route)
    .FirstOrDefault();
```

---

## Reuse model metadata

Map an external catalog (LiteLLM, GitHub Models, provider feeds) into an application-owned typed record.
At composition time, bind each catalog entry to a required client to create the runtime `ChatRoute`.
Catalog parsing and policy metadata remain the caller's job; here is a compact LiteLLM-style loader.

```csharp
using System.Text.Json;

public sealed record ModelCatalogEntry(
    string RouteName,
    string? Provider,
    string ModelId,
    ReasoningEffort? DefaultReasoningEffort,
    int? ContextWindowTokens,
    decimal? InputCostPerMillionTokens,
    decimal? OutputCostPerMillionTokens,
    Uri MetadataSource)
{
    public ChatRoute Bind(IChatClient client) =>
        new(RouteName, client, ModelId, DefaultReasoningEffort);
}

static IReadOnlyDictionary<string, ModelCatalogEntry> LoadLiteLlm(JsonDocument doc)
{
    var catalog = new Dictionary<string, ModelCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (JsonProperty model in doc.RootElement.EnumerateObject())
    {
        if (model.Name == "sample_spec")
        {
            continue;
        }

        JsonElement v = model.Value;
        catalog[model.Name] = new ModelCatalogEntry(
            RouteName: model.Name,
            Provider: v.TryGetProperty("litellm_provider", out var p) ? p.GetString() : null,
            ModelId: model.Name,
            DefaultReasoningEffort: null,
            ContextWindowTokens: v.TryGetProperty("max_input_tokens", out var mit) ? mit.GetInt32() : null,
            InputCostPerMillionTokens: v.TryGetProperty("input_cost_per_token", out var ic)
                ? ic.GetDecimal() * 1_000_000 : null,
            OutputCostPerMillionTokens: v.TryGetProperty("output_cost_per_token", out var oc)
                ? oc.GetDecimal() * 1_000_000 : null,
            MetadataSource: new Uri("https://github.com/BerriAI/litellm"));
    }

    return catalog;
}

IReadOnlyDictionary<string, ModelCatalogEntry> catalog =
    LoadLiteLlm(JsonDocument.Parse(json));

// Both logical models use the same client; Bind carries each model id into its runtime route.
ChatRoute[] routes =
[
    catalog["gpt-4o-mini"].Bind(sharedOpenAIClient),
    catalog["gpt-4o"].Bind(sharedOpenAIClient),
];

IChatClient router = new ComplexityRoutingClient(
    routes,
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
    new ChatRoute("us", usRouter),
    new ChatRoute("eu", euRouter),
]);
```

The leaf client's response flows back unchanged through every router.

For standard OpenTelemetry spans, wrap the router to trace the overall request and wrap each route client
to trace individual attempts. See [routing-chat-client.md](./routing-chat-client.md#opentelemetry).

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
| **Pin** a request to a specific model/route | `ChatOptions.ModelId` (read in your `SelectRouteAsync`) |
| **Prune a provider** on an auth error | interpret `lastException` and consult an app-owned provider map |
| Route across **different providers/clients** | construct each route with its required `IChatClient` |
| **Nested** region/tenant → model routing | a route whose `Client` is another `RoutingChatClient` |
| Reuse **model metadata** | typed app-owned catalog entries with a method that creates a runtime `ChatRoute` |
