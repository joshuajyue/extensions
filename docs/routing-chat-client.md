# RoutingChatClient: mechanism, selectors, and stickiness

> `RoutingChatClient` and its selectors are experimental (`MEAI001`).

## What it is

`RoutingChatClient` is an `IChatClient` that owns **N** candidate models and forwards each
request to one of them. It is the routing **mechanism**: it holds the candidates, caches
decisions, walks fallbacks on failure, and records the chosen model on the response. It holds
**no opinion** about which model is better — that is delegated entirely to a swappable
selection **policy**, an `IChatRouteSelector`.

When no selector is supplied the default is deterministic and opinion-free: it honors
`ChatOptions.ModelId` when set (matching a model's `ModelId` or `Name`), otherwise it uses the
first registered model. This is the shape that ships "empty": the mechanism is stable, while
concrete selection policies are experimental, swappable add-ons (or user-provided).

`RoutingChatClient` implements `IChatClient` directly rather than deriving from
`DelegatingChatClient`, because it owns multiple model clients instead of wrapping a single
`InnerClient`.

## Mechanism vs. policy

Every knob lands on one side of a single litmus test:

> Does it require knowing **which model is better**, or **what the user is asking for**?
> If yes, it is a **selector** (policy). If it is only about **identity / scope / caching /
> transport**, it is the **router** (mechanism).

- "the conversation id is the cache key" → identity → **router** (`RoutingStickiness`).
- "a jump in complexity should re-route" → judges the request → **selector**
  (`ChatRoutePlan.RemainsValid`).

This keeps the empty-selection abstraction intact: the router never grows an opinion.

## Routing is a tree, not a list

Because each candidate is itself an `IChatClient`, a routing pipeline forms a **tree**:

- **Outer / shared middleware:** wrap the whole router with
  `router.AsBuilder().UseX().Build()`.
- **Per-branch middleware:** each candidate's `IChatClient` is its own built pipeline.
- **Decision middleware:** selectors compose as decorators (for example, sticky → semantic →
  default).
- A branch may itself be a `RoutingChatClient`, giving a recursive routing tree.

## Building a router

```csharp
using Microsoft.Extensions.AI;

// Provider/model clients created by your app (OpenAI, Azure OpenAI, etc.).
IChatClient gpt53Client = ...;
IChatClient gpt4oMiniClient = ...;
IChatClient privateModelClient = ...;

var catalog = new RoutingChatModelCatalog(
[
    new RoutingChatModel(
        name: "openai:gpt-5.3",
        providerName: "openai",
        modelId: "gpt-5.3",
        traits: RoutingChatModelTraits.Reasoning | RoutingChatModelTraits.ToolCalling,
        inputTokenCostPerMillion: 10,
        outputTokenCostPerMillion: 30),
    new RoutingChatModel(
        name: "openai:gpt-4o-mini",
        providerName: "openai",
        modelId: "gpt-4o-mini",
        traits: RoutingChatModelTraits.ToolCalling | RoutingChatModelTraits.Vision,
        inputTokenCostPerMillion: 1,
        outputTokenCostPerMillion: 3,
        typicalLatency: TimeSpan.FromMilliseconds(400)),
]);

IChatClient root = new RoutingChatClientBuilder(catalog)
    .AddFromCatalog("openai:gpt-5.3", gpt53Client)
    .AddFromCatalog("openai:gpt-4o-mini", gpt4oMiniClient)
    .AddModel(
        name: "private-fast-model",
        client: privateModelClient,
        providerName: "contoso",
        modelId: "contoso-fast",
        inputTokenCostPerMillion: 1,
        outputTokenCostPerMillion: 2,
        typicalLatency: TimeSpan.FromMilliseconds(250))
    .UseSelector(RuleBasedChatRouteSelector.Instance)   // optional; omit for the opinion-free default
    .UseStickiness(RoutingStickiness.ByConversationId)
    .Build();

// Cross-cutting middleware is layered after the routing root is created.
IChatClient client = root
    .AsBuilder()
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

var options = new ChatOptions { ConversationId = "conv-123" };
var response = await client.GetResponseAsync(messages, options);
```

The user-facing noun is **"model"** (`AddModel`). A `RoutingChatModel` carries optional
**objective** metadata only — stable name, provider, model id, capability traits (`ToolCalling`,
`Vision`, `Reasoning` — each readable straight from a provider catalog), input/output token
cost, a context-window hint (`MaxInputTokens`), a latency hint, source URL, and update time. The
metadata is advisory: the mechanism never interprets it, only a selector does.

Subjective or custom dimensions (e.g., a quality score, benchmark results, region, or any
provider-specific signal a selector needs) are **not** first-class fields. Put them in
`AdditionalProperties` — the modular extension bag — and have your selector read them:

```csharp
new RoutingChatModel(
    name: "openai:gpt-5.3",
    providerName: "openai",
    modelId: "gpt-5.3",
    traits: RoutingChatModelTraits.Reasoning | RoutingChatModelTraits.ToolCalling,
    additionalProperties: new() { ["quality"] = 0.95, ["region"] = "us-east" });
```

The default ship keeps the first-class surface objective and small (LiteLLM-style capability
categories + cost + a latency slot); anything subjective lives in `AdditionalProperties` so it
can evolve without changing the API.

## Selectors (policy)

A selector implements:

```csharp
ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default);
```

A `ChatRoutePlan` is an **ordered list** of models: the first is the primary route and the
rest are fallbacks tried in order on failure. It also carries an optional `RemainsValid`
predicate (see Stickiness).

Built-in, experimental policies:

- **`RuleBasedChatRouteSelector`** — a deterministic, metadata-driven heuristic. It infers the
  required capability traits from the request (tool use → `ToolCalling`; reasoning options →
  `Reasoning`) and ranks models by **context fit** (models whose `MaxInputTokens` can't hold the
  estimated prompt are dropped), then required traits, then cost, with latency as the tie-breaker.
  Ties preserve registration order.
- **`ComplexityChatRouteSelector`** — a deterministic port of LiteLLM's complexity router; see
  [Complexity-based routing](#complexity-based-routing-litellm-style) below.
- **`SemanticChatRouteSelector`** — embedding-based; see
  [semantic-chat-client-selection.md](semantic-chat-client-selection.md).

Inline delegates are supported via the builder:

```csharp
builder.UseSelector(ctx => new ChatRoutePlan(ctx.Models[0]));                  // sync
builder.UseSelector((ctx, ct) => SomeAsyncSelectionAsync(ctx, ct));           // async
```

A selector must route to one of the **registered** model instances; routing to an unknown
model throws.

## Complexity-based routing (LiteLLM-style)

`ComplexityChatRouteSelector` is an experimental selector that ports LiteLLM's
[auto-routing complexity router](https://docs.litellm.ai/docs/proxy/auto_routing). It scores
each request with deterministic, sub-millisecond keyword/pattern rules (no I/O, no embeddings),
classifies it into a `ChatComplexityTier`, and routes to the model you mapped to that tier.

The tier → model map is **explicit** and required at construction (it makes no judgement about
which model is "better" — that's your call), with an optional default for unmapped tiers:

```csharp
var selector = new ComplexityChatRouteSelector(
    new Dictionary<ChatComplexityTier, string>
    {
        [ChatComplexityTier.Simple]    = "gpt-4o-mini",
        [ChatComplexityTier.Medium]    = "gpt-4o",
        [ChatComplexityTier.Complex]   = "gpt-4o",
        [ChatComplexityTier.Reasoning] = "o3",
    },
    defaultModel: "gpt-4o");

builder.UseSelector(selector);
```

The tiers are `Simple`, `Medium`, `Complex`, and `Reasoning`. Classification sums seven
weighted signals (defaults mirror LiteLLM), then compares the score against tier boundaries:

| Dimension | Detects | Default weight |
|---|---|---|
| Token count | short (`< 15`) vs. long (`> 400`) prompts | `0.10` |
| Code presence | `function`, `class`, `api`, `database`, … | `0.30` |
| Reasoning markers | `step by step`, `think through`, `analyze`, … | `0.25` |
| Technical terms | `architecture`, `distributed`, `encryption`, … | `0.25` |
| Simple indicators | `what is`, `define`, `hello`, … (**lowers** the score) | `0.05` |
| Multi-step patterns | `first … then`, numbered steps | `0.03` |
| Question complexity | multiple `?` | `0.02` |

**Special rule:** two or more distinct reasoning markers force the `Reasoning` tier outright,
regardless of the weighted score. Every weight, boundary, token threshold, and keyword list is
overridable via `ComplexityRouterOptions`, so the scoring can be tuned (or fully replaced with
your own vocabulary) without changing the selector:

**Matching details (faithful to LiteLLM):** single-word keywords match on **word boundaries**
(so `api` does not fire inside `capital`) while multi-word phrases match as substrings; each
keyword dimension contributes a **bucketed** score based on how many distinct keywords hit (not a
linear count); multi-step patterns are **regexes**; the code, technical, and simple signals also
read the system prompt, whereas reasoning markers, token count, and question count consider only
the user message.

```csharp
var options = new ComplexityRouterOptions
{
    TechnicalTerms = ["frobnicate", "wibble"],   // your domain vocabulary
    SimpleToMediumThreshold = 0.20,              // require a bit more before leaving Simple
};
var selector = new ComplexityChatRouteSelector(tierMap, options: options);
```

`ClassifyTier(messages)` is public if you want the tier without routing. Because complexity
routing is just another selector, it composes with the rest of the mechanism: fallback, response
stamping, stickiness, and nesting all apply unchanged. (It does **not** apply the
`RuleBasedChatRouteSelector` context-window hard-filter — pair it with capacity-aware tier
mappings if a tier's model has a small context window.)

## Model metadata and currency

Selectors need per-model facts (capabilities, context window, cost) and those facts change as
providers ship models. `RoutingChatModel` carries advisory metadata for exactly this — traits,
token cost, context window (`MaxInputTokens`), `SourceUri`, and `UpdatedAt` — which a
`RoutingChatModelCatalog` can store as client-less entries and bind to a client later via
`WithClient`.

To avoid hand-authoring that metadata, the experimental **`LiteLlmModelCatalog`** adapter maps
the LiteLLM catalog (`model_prices_and_context_window.json`, ~2900 entries) into
`RoutingChatModel` entries:

```csharp
using var http = new HttpClient();
await using Stream json = await http.GetStreamAsync(
    "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");

IReadOnlyList<RoutingChatModel> models = LiteLlmModelCatalog.Load(
    json,
    new LiteLlmCatalogOptions
    {
        ChatModelsOnly = true,                  // skip embedding/image/audio entries
        UpdatedAt = DateTimeOffset.UtcNow,      // provenance for currency-aware selectors
        IncludeModel = name => name.StartsWith("openai", StringComparison.Ordinal),
    });

var catalog = new RoutingChatModelCatalog(models);
RoutingChatModel gpt4o = catalog.CreateModel("gpt-4o", myOpenAiClient);
```

It maps **only objective fields**: `supports_function_calling`/`supports_tool_choice` →
`ToolCalling`, `supports_vision`/`supports_image_input` → `Vision`, `supports_reasoning` →
`Reasoning`; per-token cost → `InputTokenCostPerMillion`/`OutputTokenCostPerMillion`;
`max_input_tokens` → the first-class `MaxInputTokens` (context window); and mode, deprecation
date, and other capability flags into `AdditionalProperties` keyed with
`LiteLlmModelCatalog.MetadataKeyPrefix` (`"litellm."`). It deliberately **never** infers latency
or quality (`TypicalLatency`, or any subjective score) because the catalog has no such data —
those remain the selector's judgment. This makes it a natural **hard-filter** input
(eliminate models that can't satisfy a request, or whose context window is too small), leaving the
soft ranking to policy.

## Fallback (circuit breaking)

The router walks `ChatRoutePlan.OrderedModels`: if the primary throws, it tries the next, and
so on. The exception of the **last** model propagates. The model that ultimately succeeds is
the one stamped on the response. `ChatRouteContext.PreviousAttempt` and `ChatRouteAttempt`
exist so an advanced selector can implement adaptive behavior (for example, circuit breaking);
the default behavior simply walks the static plan.

**Streaming caveat:** fallback applies only *before the first update is yielded*. Once a
streaming response has started, no fallback occurs.

## Stickiness = caching (router) + invalidation (selector)

Stickiness is split across the mechanism/policy line:

- **Caching scope (router).** `RoutingStickiness` selects how a decision is cached:
  - `EveryCall` — run the selector for every request.
  - `PerInstance` — run once per `RoutingChatClient` and reuse for all requests.
  - `ByConversationId` — run once per `ChatOptions.ConversationId` and reuse within that
    conversation. If the conversation id is missing, behavior falls back to `EveryCall`.

  These are pure cache scopes with no opinion about model fitness.

- **Invalidation (selector).** Before reusing a cached decision, the router awaits the plan's
  optional `RemainsValid(context, cancellationToken)` predicate. If it returns `false`, the
  router re-runs the selector even on a sticky hit. This is how drift-aware re-selection ("the
  user's needs changed a lot") is expressed *without* the router knowing what "drift" means —
  the selector defines it, using cheap signals (length, keywords, `Options.Tools`) or async
  re-embedding. When `RemainsValid` is `null`, a cached decision is a pure pin for its scope.

  ```csharp
  builder.UseSelector(ctx =>
  {
      RoutingChatModel model = ChooseModel(ctx);
      int baseline = TotalLength(ctx.Messages);

      // Re-route if the conversation's size roughly doubles (a cheap proxy for changed needs).
      return new ChatRoutePlan(model, (latest, _) =>
          new ValueTask<bool>(TotalLength(latest.Messages) < baseline * 2));
  });
  ```

  Never encode a drift threshold as a router stickiness *mode* — that would smuggle selection
  back into the mechanism.

## Decision output

The chosen model is recorded on the **response**, not mutated into the forwarded request:

- `ChatResponse.AdditionalProperties` / first streaming update's `AdditionalProperties`:
  - `RoutingChatClient.SelectedModelNameKey` (`"routing.selected_model"`)
  - `RoutingChatClient.SelectedModelIdKey` (`"routing.selected_model_id"`)
  - `RoutingChatClient.SelectedProviderNameKey` (`"routing.selected_provider"`)
- An `Activity.Current` tag with the same keys.

Only the provider `ModelId` is forwarded to the chosen client, and only when the caller did
not pin one. Routing internals are never written into the forwarded `ChatOptions`.

## ChatOptions as a metadata carrier

- **Inputs** (caller hints such as a pinned model or `ConversationId`) are read from
  `ChatOptions`.
- **Outputs** are response-side only (see Decision output).

## Future work

1. Document recommended ordering with other middleware (function invocation, telemetry,
   logging, caching).
2. Add end-to-end samples for single-provider multi-model, multi-provider, and sticky +
   fallback scenarios.
3. Additional selectors: cost-optimizing, round-robin (load balancing), weighted (canary).
4. Provider-specific or generated catalogs, once the data-source and provenance story is
   approved.
5. Telemetry overlays for app-measured latency, failure rate, and cost-in-practice.
