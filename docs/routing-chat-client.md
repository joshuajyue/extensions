# Route chat requests with `RoutingChatClient`

`RoutingChatClient` is an [`IChatClient`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient)
that owns several candidate models and forwards each request to one of them. It separates the routing
*mechanism* — holding the candidates, filtering them, running a policy, and walking fallbacks — from the
selection *policy* that decides which model a given request should use. The policy is a swappable
`IChatRouteSelector`; when none is supplied, routing falls back to a deterministic default.

This article describes the routing types, how to construct a router, how to write or choose a selection
policy, and how to observe routing decisions. For end-to-end, scenario-driven samples, see the
[RoutingChatClient cookbook](./routing-chat-client-cookbook.md).

> [!IMPORTANT]
> The routing types are experimental. Every public type is annotated with
> `[Experimental("MEAI001")]`, so using them produces the `MEAI001` diagnostic, and the API shape may
> change before it stabilizes. Suppress the diagnostic deliberately (for example, with
> `#pragma warning disable MEAI001`) to opt in.

> [!NOTE]
> The `ComplexityChatRouteSelector` algorithm derives from
> [ClawRouter](https://github.com/BlockRunAI/ClawRouter); the `SemanticChatRouteSelector` algorithm
> derives from [Aurelio Labs' `semantic-router`](https://github.com/aurelio-labs/semantic-router). Both
> are MIT-licensed. See [`THIRD-PARTY-NOTICES.TXT`](../THIRD-PARTY-NOTICES.TXT).

## Packages and namespaces

All routing types live in the `Microsoft.Extensions.AI` namespace, so a single
`using Microsoft.Extensions.AI;` covers them, but they ship in three packages:

| Package | Contents |
|---|---|
| `Microsoft.Extensions.AI.Abstractions` | The policy contract and metadata types: `ChatRoute`, `ChatRouteCatalog`, `IChatRouteSelector`, `ChatRouteSelector`, `ChatRouteContext`, `ChatRoutePlan`. |
| `Microsoft.Extensions.AI` | The mechanism: `RoutingChatClient`, `DelegatingRoutingChatClient`, the `UseRouting` builder extension, and `RouteFailureContext`. |
| `Microsoft.Extensions.AI.Routing` | Shipped policies and helpers: `ComplexityChatRouteSelector`, `SemanticChatRouteSelector`, `StickyChatRouteSelector`, `RouteCooldownStore`, and their options and enums. |

The dependency direction is one-way: the `Microsoft.Extensions.AI.Routing` selectors reference the core
abstractions, never the reverse. You can adopt the mechanism and write your own selector without taking
a dependency on `Microsoft.Extensions.AI.Routing`.

## How routing works

Routing is built on one distinction:

- **Mechanism** — `RoutingChatClient`. It owns the candidate routes, applies an optional candidate
  filter, invokes the selector once per request, walks fallbacks when a dispatch fails, and records the
  chosen route on the response. It holds no opinion about which model is better.
- **Policy** — `IChatRouteSelector`. All judgment about *which route is better* or *what the request
  needs* lives behind this interface. It is swappable, and optional.

A single question determines where a behavior belongs: does it require knowing *which model is better*
or *what the user is asking for*? If so, it is policy (a selector). If it concerns only identity, scope,
transport, or health, it is mechanism (the router). This keeps the mechanism stable while selection
policies remain experimental, swappable add-ons.

Each request flows through the router in a fixed order:

1. **Filter** the registered routes through the optional `canRoute` predicate to produce the candidate
   set. See [Filter candidate routes with `canRoute`](#filter-candidate-routes-with-canroute).
2. **Select** by calling the selector once with the candidate set. The selector returns a
   `ChatRoutePlan` — an ordered list of routes, primary first. See
   [Write a selection policy](#write-a-selection-policy).
3. **Dispatch** the plan's primary route. If it fails before producing output, consult `onFailure` and
   try the next route. See [Handle failures with `onFailure`](#handle-failures-with-onfailure).
4. **Stamp** the winning route onto the response and emit trace events. See
   [Observe routing decisions](#observe-routing-decisions).

Because `RoutingChatClient` is itself an `IChatClient`, and each route is bound to an `IChatClient`, a
routing pipeline forms a tree. A route's client can carry its own middleware, or be another
`RoutingChatClient`. Route coarsely at the top (for example, by region or tenant) and finely below (for
example, by complexity). Cross-cutting middleware wraps the whole router with
`router.AsBuilder().UseX().Build()`; per-branch middleware lives on each route's client; and selectors
compose as decorators.

## Create a routing client

There are two front doors. Use `RoutingChatClient` to route across **several clients** (typically
different providers). Use `UseRouting` to route across **models on one client** (same provider, different
model IDs or reasoning efforts).

### Route across clients with `RoutingChatClient`

`RoutingChatClient` is a plain `IChatClient`. Construct it with the routes it should choose between,
then layer middleware as you would over any other client — there is no routing-specific builder.

The constructor takes the following parameters:

| Parameter | Type | Description |
|---|---|---|
| `routes` | `IReadOnlyList<ChatRoute>` | The candidate routes. Each is typically bound to an `IChatClient`. |
| `selector` | `IChatRouteSelector?` | The selection policy. When `null`, the [default](#default-selection-behavior) applies. |
| `onFailure` | `Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>?` | The fallback policy, invoked on each pre-commit dispatch failure. When `null`, only the routes the selector put in the plan are retried; set it to also fall back to routes the plan omitted. |
| `canRoute` | `Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, bool>?` | The candidate filter. When `null`, every registered route is a candidate. |

```csharp
using Microsoft.Extensions.AI;

// Provider/model clients created by your app.
IChatClient openAiClient = /* ... */;
IChatClient anthropicClient = /* ... */;
IChatClient geminiClient = /* ... */;

IChatClient router = new RoutingChatClient(
[
    new ChatRoute("openai:gpt-5.3", providerName: "openai", modelId: "gpt-5.3", client: openAiClient),
    new ChatRoute("anthropic:sonnet", providerName: "anthropic", modelId: "claude-sonnet", client: anthropicClient),
    new ChatRoute("google:gemini-flash", providerName: "google", modelId: "gemini-2.0-flash", client: geminiClient),
],
    selector: new ComplexityChatRouteSelector(tierMap),
    onFailure: ctx => ctx.Remaining);

// Cross-cutting middleware layers over the router like any other IChatClient.
IChatClient client = router
    .AsBuilder()
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

ChatResponse response = await client.GetResponseAsync(messages);
```

### Route across models on one client with `UseRouting`

When every route targets the same inner client and differs only by `ModelId` (or `ReasoningEffort`),
use the `UseRouting` builder extension. It adds a `DelegatingRoutingChatClient` that forwards the chosen
route's model ID to a single inner client instead of holding one client per route. `UseRouting` accepts
the same `selector`, `onFailure`, and `canRoute` parameters as the `RoutingChatClient` constructor.

```csharp
IChatClient client = openAiClient
    .AsBuilder()
    .UseRouting(
    [
        new ChatRoute("gpt-5.5-low", modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.Low),
        new ChatRoute("gpt-5.5-medium", modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.Medium),
        new ChatRoute("gpt-5.5-high", modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.High),
    ],
        selector: new ComplexityChatRouteSelector(tierMap),
        onFailure: ctx => ctx.Remaining)
    .Build();
```

### Register with dependency injection

Both front doors compose with the standard `AddChatClient` / `ChatClientBuilder` infrastructure, so no
routing-specific registration helper is required:

```csharp
services.AddChatClient(serviceProvider => new RoutingChatClient(
[
    new ChatRoute("openai:gpt-5.3", client: serviceProvider.GetRequiredKeyedService<IChatClient>("gpt-5.3")),
    new ChatRoute("openai:gpt-4o-mini", client: serviceProvider.GetRequiredKeyedService<IChatClient>("gpt-4o-mini")),
]))
    .UseFunctionInvocation()
    .UseOpenTelemetry();
```

## Define routes with `ChatRoute`

A `ChatRoute` is the unit a router chooses between. It carries a required `Name` (a stable, router-local
alias) and optional, advisory metadata. The router itself reads only route identity to dispatch; the
selector and `canRoute` predicate decide how to use the rest.

| Member | Type | Description |
|---|---|---|
| `Name` | `string` | Required. The route's stable identifier and telemetry alias. A selector routes by name. |
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
| `Client` | `IChatClient?` | The client that serves the route. Required for `RoutingChatClient`; omit for `UseRouting`. |

The first-class surface is intentionally objective and small: provider and model identity, cost,
context window, latency, and provenance. Subjective or custom dimensions — a quality score, benchmark
results, region, or any provider-specific signal — are not first-class fields. Put them in
`AdditionalProperties` under app-specific keys and have your selector read them. Capability tokens are
just open strings under an app-chosen key, so applications can add their own objective tokens without a
library change:

```csharp
new ChatRoute(
    name: "openai:gpt-5.3",
    providerName: "openai",
    modelId: "gpt-5.3",
    additionalProperties: new AdditionalPropertiesDictionary
    {
        ["capabilities"] = new[] { "reasoning", "function_calling", "legal_reviewed" },
        ["quality"] = 0.95,
        ["region"] = "us-east",
    });
```

### Reuse metadata with `ChatRouteCatalog`

`ChatRouteCatalog` stores reusable, client-less `ChatRoute` metadata that you bind to a client later.
Look up an entry with `Get` (or `TryGet`) and attach a client with `WithClient` or the catalog's
`CreateRoute`. This separates the *catalog* of known models from the *wiring* of a specific router.

```csharp
var catalog = new ChatRouteCatalog(
[
    new ChatRoute("openai:gpt-5.3", providerName: "openai", modelId: "gpt-5.3",
        inputTokenCostPerMillion: 10, outputTokenCostPerMillion: 30),
    new ChatRoute("openai:gpt-4o-mini", providerName: "openai", modelId: "gpt-4o-mini",
        inputTokenCostPerMillion: 1, outputTokenCostPerMillion: 3),
]);

IChatClient router = new RoutingChatClient(
[
    catalog.Get("openai:gpt-5.3").WithClient(gpt53Client),
    catalog.Get("openai:gpt-4o-mini").WithClient(gpt4oMiniClient),
]);
```

Mapping an external catalog (LiteLLM, GitHub Models, provider feeds) into `ChatRoute` entries is left to
the caller, because catalog formats and provenance requirements differ. The
[cookbook](./routing-chat-client-cookbook.md) shows a worked LiteLLM loader.

## Write a selection policy

A selection policy implements `IChatRouteSelector`:

```csharp
ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default);
```

The input, `ChatRouteContext`, carries everything known about the request and the candidates:

| Member | Type | Description |
|---|---|---|
| `Messages` | `IEnumerable<ChatMessage>` | The request messages. |
| `Options` | `ChatOptions?` | The request options (including any caller-pinned `ModelId`). |
| `Routes` | `IReadOnlyList<ChatRoute>` | The candidate routes, already narrowed by any `canRoute` filter. |

The output, `ChatRoutePlan`, is an ordered list of routes: the first is the primary and the rest are
fallbacks tried in order. Construct it from a single route or an ordered list, optionally with decision
metadata for [telemetry](#observe-routing-decisions):

| Member | Type | Description |
|---|---|---|
| `OrderedRoutes` | `IReadOnlyList<ChatRoute>` | The plan, primary first, then fallbacks. |
| `DecisionMetadata` | `IReadOnlyDictionary<string, object>?` | Optional rationale a selector attaches to the decision (surfaced as trace-event tags). |

A selector must route to one of the **registered** route instances; routing to an unknown route throws.

### Create an inline selector

For trivial policies, wrap a lambda with the `ChatRouteSelector.Create` factory instead of writing a
class. Both synchronous and asynchronous forms are supported:

```csharp
IChatRouteSelector sync = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));
IChatRouteSelector async = ChatRouteSelector.Create((ctx, ct) => SelectAsync(ctx, ct));

var router = new RoutingChatClient(routes, selector: sync);
```

### Default selection behavior

When no selector is supplied, selection is deterministic and opinion-free: the router honors
`ChatOptions.ModelId` when set (matching a route's `ModelId` or `Name`); otherwise it uses the first
registered route. This is the shape a router ships with "empty" — the mechanism is stable while concrete
policies remain opt-in.

## Filter candidate routes with `canRoute`

`canRoute` is an optional predicate that decides, per request, which routes are eligible before the
selector runs:

```csharp
Func<ChatRoute, IEnumerable<ChatMessage>, ChatOptions?, bool>? canRoute
```

Given a route and the request's messages and options, it returns whether the router may consider that
route this turn. When `canRoute` is `null` (the default), every registered route is a candidate; the
router ships no filtering vocabulary of its own.

The predicate's output is the single candidate set consumed by *both* the selector
(`ChatRouteContext.Routes`) and the fallback walk (`RouteFailureContext.Remaining`), so a rule written
once holds in both places. It is the single seam for two independent concerns:

- **Correctness (capability)** — for example, "this request carries an image, so only vision-capable
  routes qualify," expressed over whatever tokens your routes declare in `AdditionalProperties`.
- **Availability (health)** — for example, "this route is cooling after a 429, so skip it," by reading a
  `RouteCooldownStore` (or your own circuit-breaker state) that `onFailure` writes to.

The filter is **soft**: if the predicate admits no route for a request, the router falls through to the
full candidate set rather than stranding the request. So `canRoute` narrows *preference*, not a
*guarantee*. When a request must never reach a given route, enforce that in the selector or `onFailure`.
For complete availability and capability recipes, see the
[cookbook's `canRoute` section](./routing-chat-client-cookbook.md#4-the-canroute-candidate-filter).

## Handle failures with `onFailure`

A selector that naturally picks a single route — such as a complexity classifier — has no honest ranking
of the other routes, so it returns a one-route plan. `onFailure` gives such a selector resilience. It is
the router's fallback policy, invoked on **each** pre-commit dispatch failure with a
`RouteFailureContext`:

| Member | Type | Description |
|---|---|---|
| `Route` | `ChatRoute` | The route that just failed. |
| `Exception` | `Exception` | The thrown exception, unclassified — you interpret it. |
| `AttemptNumber` | `int` | The 1-based count of routes tried so far. |
| `Remaining` | `IReadOnlyList<ChatRoute>` | The still-untried candidates (the plan's tail plus registered routes the plan omitted). |
| `Options` | `ChatOptions?` | The request options. |
| `Messages` | `IEnumerable<ChatMessage>` | The request messages. |

Return the routes to try next, in order — any subset of `Remaining`, reordered as you like — or
`null`/empty to stop and rethrow. Returned routes are validated to registered routes and de-duped
against those already attempted, so routing always terminates. When `onFailure` is `null`, the router
retries only the routes the selector put in the plan; routes the plan omitted are never tried.
Supplying `onFailure` lets you fall back to those omitted routes too — they appear in `Remaining`. When
the plan and any `onFailure` fallbacks are exhausted, the last route's exception propagates. The route
that ultimately succeeds is the one stamped on the response.

```csharp
var router = new RoutingChatClient(
[
    new ChatRoute("fast", client: fastClient),
    new ChatRoute("smart", client: smartClient),
],
    selector: new ComplexityChatRouteSelector(tierMap), // picks one route
    onFailure: ctx => ctx.Remaining);                   // on failure, try the rest in order
```

> [!NOTE]
> `onFailure` applies only *before the first update is yielded*. Once a streaming response has started,
> no re-routing occurs. Cancellation is never treated as a failure.

## Built-in selectors

The `Microsoft.Extensions.AI.Routing` package ships three selectors. All are optional and swappable.

### Complexity selector

`ComplexityChatRouteSelector` scores each request with deterministic, sub-millisecond keyword and
pattern rules (no I/O, no embeddings), classifies it into a `ChatComplexityTier`
(`Simple`, `Medium`, `Complex`, `Reasoning`), and routes to the route mapped to that tier. The
tier-to-route map is explicit and required — the selector makes no judgment about which route is
"better":

```csharp
var selector = new ComplexityChatRouteSelector(
    new Dictionary<ChatComplexityTier, string>
    {
        [ChatComplexityTier.Simple]    = "gpt-4o-mini",
        [ChatComplexityTier.Medium]    = "gpt-4o",
        [ChatComplexityTier.Complex]   = "gpt-4o",
        [ChatComplexityTier.Reasoning] = "o3",
    },
    defaultRoute: "gpt-4o");

var router = new RoutingChatClient(routes, selector: selector);
```

#### How the classifier works

1. **Pick the text.** The classifier reads the **last user message** and the **last system message**;
   earlier turns are ignored so it classifies the *current* ask. All matching is case-insensitive.
2. **Score seven dimensions.** Each dimension produces a raw score, multiplied by its weight; the
   products are summed into one number. There is no clamping — the sum can go below 0 (simple prompts)
   or above 1 (very complex ones).
3. **Apply the reasoning override.** If the user message hits `ReasoningMarkerOverrideCount` (default 2)
   or more distinct reasoning markers, the request is forced to the `Reasoning` tier regardless of the
   weighted sum.
4. **Map the score to a tier** using three boundaries.

The seven dimensions, each with the text it reads, how its raw score is computed, and its default
weight ("distinct matches" means the number of *different* keywords that hit, not total occurrences):

| Dimension | Text | Raw score | Weight |
|---|---|---|---|
| Token count | user message | `len/4` chars→tokens: `< 15` → −1.0, `> 400` → +1.0, else 0. Short prompts are penalized toward `Simple`. | 0.10 |
| Code presence | system + user | distinct hits: 0 → 0, 1 → 0.5, ≥ 2 → 1.0. | 0.30 |
| Reasoning markers | user only | distinct hits: 0 → 0, 1 → 0.7, ≥ 2 → 1.0. | 0.25 |
| Technical terms | system + user | distinct hits: < 2 → 0, 2–3 → 0.5, ≥ 4 → 1.0. | 0.25 |
| Simple indicators | system + user | 0 → 0, ≥ 1 → −1.0. Lowers the score. | 0.05 |
| Multi-step patterns | system + user | any regex matches → 0.5, else 0. | 0.03 |
| Question complexity | user message | more than 3 `?` characters → 0.5, else 0. | 0.02 |

After the override check, the weighted sum is bucketed into a tier using the three thresholds:

| Score range | Tier |
|---|---|
| `< 0.15` (`SimpleToMediumThreshold`) | `Simple` |
| `< 0.35` (`MediumToComplexThreshold`) | `Medium` |
| `< 0.60` (`ComplexToReasoningThreshold`) | `Complex` |
| `≥ 0.60` | `Reasoning` |

Two properties of the scoring are worth noting. First, dimensions use **buckets, not linear counts**: a
dimension jumps between fixed values at integer thresholds, so one strong code keyword already
contributes 0.5 and two saturate it at 1.0 — which makes the score robust to keyword-stuffing. Second,
**code presence dominates** at weight 0.30, so two code keywords alone (0.30) are already enough to
leave `Simple`; code-shaped prompts route up aggressively by design.

Matching semantics:

- **Single-word keywords match on word boundaries**, so `api` fires on `the api` but not inside
  `capital`. Multi-word phrases (for example, `pull request`) match as plain substrings.
- **Multi-step patterns are regexes** (case-insensitive, with a 100 ms match timeout), so `1. … 2. …`
  numbering and `first … then` phrasing both fire.
- **Text scope differs per dimension on purpose**: the system prompt contributes to code, technical,
  simple, and multi-step signals, but reasoning markers, token count, and question count read only the
  user message — so a system prompt can never single-handedly force the `Reasoning` tier.

#### Customizing

Every weight, boundary, token threshold, keyword list, and regex is a property on
`ComplexityRouterOptions`, so you can retune the math or swap in your own vocabulary without subclassing.
Because the keyword lists are just data, replacing `TechnicalTerms` with your domain words turns this
into a domain-aware router:

```csharp
var options = new ComplexityRouterOptions
{
    TechnicalTerms = ["frobnicate", "wibble"],   // your domain vocabulary
    SimpleToMediumThreshold = 0.20,              // require a bit more before leaving Simple
};
var selector = new ComplexityChatRouteSelector(tierMap, options: options);
```

`ClassifyTier(messages)` is public if you want the tier without routing. Because a tier classifier picks
exactly one route, its plan is a single route — pair it with `onFailure` for resilience.

### Semantic selector

`SemanticChatRouteSelector` routes by **semantic similarity**. You give each route a small set of
representative example phrases — its *profile* — and at request time the selector embeds the user's
message, measures how close it is to each route's phrases, and routes to the closest match. It needs
only an `IEmbeddingGenerator<string, Embedding<float>>`; there is no extra LLM classification call. It
implements the `semantic-router` routing algorithm.

```csharp
// Any embedding generator; wrap it with caching to amortize cost.
IEmbeddingGenerator<string, Embedding<float>> embedder =
    new EmbeddingGeneratorBuilder<string, Embedding<float>>(rawEmbedder)
        .UseCaching()
        .Build();

// The dictionary key is the route name (the same name passed to ChatRoute).
var profiles = new Dictionary<string, IReadOnlyList<string>>
{
    ["openai:gpt-4o-mini"] = ["documentation", "tutorials", "simple questions", "short answers"],
    ["openai:gpt-5.3"]     = ["complex reasoning", "advanced algorithms", "multi-step code", "architecture"],
};

var selector = new SemanticChatRouteSelector(
    embedder,
    profiles,
    defaultRoute: "openai:gpt-4o-mini",           // used when nothing passes the threshold
    options: new SemanticRouterOptions
    {
        TopK = 5,
        Aggregation = SemanticRouteAggregation.Mean,
        ScoreThreshold = 0.3f,
    });
```

#### How the selector works

1. **Extract the query.** The selector takes the last user message. If there is none, it returns the
   default (or first registered) route — there is no text to route on.
2. **Embed the profiles once.** All profile phrases are embedded in a single call and cached; a
   transient embedding failure is not cached, so it is retried on the next request.
3. **Score every phrase.** The query is embedded once and scored against every profile phrase with
   cosine similarity (`System.Numerics.Tensors.TensorPrimitives.CosineSimilarity`, so vectors need not
   be pre-normalized). Phrases belonging to a route that is no longer registered are skipped.
4. **Keep the global top-K.** The highest `TopK` phrase matches across all routes survive (default 5).
   A route must place a phrase in this global shortlist to be considered.
5. **Aggregate per route.** The surviving matches are grouped by route and reduced with `Aggregation` —
   `Mean` (default), `Sum`, or `Max`.
6. **Pick the winner.** Routes are ranked by aggregated score; the first whose score meets its threshold
   (`ScoreThreshold`, or a per-route override from `ScoreThresholdByRoute`) wins. If none qualifies, the
   `defaultRoute` (or first registered route) is used.
7. **Order the plan.** The winner is primary; the remaining routes follow by descending score, becoming
   the fallback chain. Because the plan is genuinely ranked, its tail is a meaningful fallback order.

`SemanticRouterOptions` tunes the algorithm:

| Property | Default | Description |
|---|---|---|
| `TopK` | 5 | How many of the globally highest phrase matches feed aggregation. Lower is more winner-take-all; higher averages over more evidence. |
| `Aggregation` | `Mean` | `Mean` rewards consistent matching; `Max` rewards a single strong match; `Sum` rewards matching in many ways (and favors routes with more phrases). |
| `ScoreThreshold` | 0.3 | The minimum aggregated cosine similarity (range −1 to 1) a route must reach to be chosen. Set to 0 or below to always route to the best-scoring route. |
| `ScoreThresholdByRoute` | `null` | Per-route threshold overrides, to hold a specific route to a higher bar. |

The selector is a good fit when there is a large cost differential between routes and enough request
volume for the embedding cost to amortize (especially with a caching embedder). It is a poor fit when
all routes cost roughly the same, when any extra embedding call is unacceptable, or when queries are
homogeneous — the deterministic complexity selector or the default is enough in those cases.

### Sticky selector

`StickyChatRouteSelector` layers conversation stickiness onto *any* inner selector without holding
state itself. Each turn it calls a `getPins` callback; if the returned route names resolve to current
candidates, it pins to them, otherwise it defers to the inner selector. Both the pin state and the
trigger to set or clear it belong to your application. See the
[cookbook](./routing-chat-client-cookbook.md#6c-making-it-sticky-pin-a-conversation-to-a-route) for a
complete, stateless stickiness pattern and guidance on choosing a stable session key.

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
accumulates instead — each router prepends the route it selected as the response unwinds. Identity
answers "who answered"; the path answers "how it got there".

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

- **`RoutingChatClient.DecisionEventName` (`routing.decision`)** — one per request, describing the
  selector's decision. It carries the `routing.selected_route` tag plus any decision rationale the
  selector attached via `ChatRoutePlan.DecisionMetadata`. The shipped selectors emit
  `routing.complexity.tier` (the classified tier), `routing.semantic.score` (the winning aggregated
  similarity), and `routing.pinned` (set by `StickyChatRouteSelector` when a turn was pinned).
- **`RoutingChatClient.AttemptEventName` (`routing.attempt`)** — one per route actually attempted, in
  order, capturing the fallback timeline. Tags: `routing.attempt.ordinal`, `routing.attempt.route`,
  `routing.attempt.model_id`, `routing.attempt.provider`, `routing.attempt.outcome`
  (`success`, `fallback`, or `error`), `routing.attempt.duration_ms`, and — on failure —
  `routing.attempt.error_type`.

The decision event answers *why this route*; the attempt events answer *what the router did*. To capture
them, subscribe to the source — for example, add `"Microsoft.Extensions.AI.Routing"` to your
OpenTelemetry tracer provider, or attach an `ActivityListener` — or run the router within an outer
sampled activity (such as the span from `UseOpenTelemetry`). The event names are public so consumers can
filter by name; the decision-metadata tag keys are a telemetry detail, so observe them through tracing
rather than binding to them in code.

## Related content

- [RoutingChatClient cookbook](./routing-chat-client-cookbook.md) — end-to-end samples for every
  archetype: cross-provider and single-provider routing, custom selectors, `onFailure` with cooldowns
  and circuit breakers, `canRoute` capability filters, catalog ingestion, and the semantic, complexity,
  and sticky selectors.
- [`Microsoft.Extensions.AI` libraries](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)
