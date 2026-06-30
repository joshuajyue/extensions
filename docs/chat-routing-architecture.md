# Chat Routing — architecture deep dive

This document explains the `ChatRouting` feature in `Microsoft.Extensions.AI` **file by file**.
It is a companion to [`routing-chat-client.md`](./routing-chat-client.md) (which is the user-facing
how-to) and [`semantic-chat-client-selection.md`](./semantic-chat-client-selection.md). Here the goal
is to explain *why each type exists* and *how the pieces fit together*, in enough depth that a new
contributor can reason about — and safely extend — the design.

> All public types in this folder are marked `[Experimental(AIRoutingChat)]` (diagnostic id
> `MEAI001`). The shape may change before it stabilizes.

---

## The one big idea: mechanism vs. policy

Everything in `ChatRouting` is a consequence of one deliberate split:

- **The mechanism** — `RoutingChatClient`. An *opinion-free* orchestrator. It owns the candidate
  models, caches decisions, walks fallbacks, forwards options, and stamps the response. It has **no
  knowledge** of which model is "better."
- **The policy** — `IChatRouteSelector`. The *swappable brain*. **All** judgment about which model to
  pick lives behind this interface.

This is what lets the product **ship an API with an empty selector**: with no policy supplied, the
mechanism still works using a deterministic, opinion-free default (honor an explicit `ModelId`, else
use the first registered model). Customers can then opt into a selector we ship (experimental), or
write their own, without the mechanism ever changing.

Because `RoutingChatClient` is *itself* an `IChatClient`, and each candidate model is bound to an
`IChatClient`, a routing pipeline is a **tree**: a candidate can carry its own middleware, or be
another `RoutingChatClient`. "A router routes to a router."

```
RoutingChatClient (IChatClient)
 ├─ model "cheap"   → IChatClient                   ← may be .Use(...)-wrapped middleware
 ├─ model "smart"   → IChatClient
 └─ model "region"  → RoutingChatClient (IChatClient)  ← a nested router
```

The folder divides cleanly into three groups:

| Group | Files |
|---|---|
| **Mechanism** | `RoutingChatClient`, `RoutingChatClientBuilder`, `RoutingChatModel`, `RoutingChatModelCatalog`, `RoutingChatModelTraits` |
| **Policy contract** | `IChatRouteSelector`, `ChatRouteSelector`, `ChatRouteContext`, `ChatRoutePlan` |
| **Shipped policies & adapters** | `SemanticChatRouteSelector` (+ `SemanticRouterOptions`, `SemanticRouteAggregation`), `ComplexityChatRouteSelector` (+ `ComplexityRouterOptions`, `ChatComplexityTier`), `ChatModelCatalog` (+ `ChatModelCatalogOptions`) |

---

## The policy contract

### `IChatRouteSelector.cs`

The contract every policy implements:

```csharp
ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken ct = default);
```

It is intentionally tiny. Input is a `ChatRouteContext` (everything known about the request and the
available models); output is a `ChatRoutePlan` (the decision). `ValueTask` keeps the synchronous,
allocation-free path cheap (the rule-based and complexity selectors complete synchronously) while
still allowing genuinely async policies (the semantic selector embeds text). The XML docs point
implementers at `ChatRouteSelector.Create` for inline delegates and at the shipped selectors.

The key design constraint: **the mechanism never reaches around this interface**. If a behavior
requires knowing "which model is better," it belongs in an implementation of this type, not in
`RoutingChatClient`.

### `ChatRouteSelector.cs` — *not* a peer of the other selectors

This is the single most common point of confusion, so it's worth stating plainly:
`ChatRouteSelector` (no `I`) is **a static factory class**, not a selector. You cannot instantiate
it; it exists only to turn a **lambda** into an `IChatRouteSelector` so trivial policies don't need
their own class:

```csharp
public static IChatRouteSelector Create(Func<ChatRouteContext, CancellationToken, ValueTask<ChatRoutePlan>> selector);
public static IChatRouteSelector Create(Func<ChatRouteContext, ChatRoutePlan> selector); // sync overload
```

Internally it wraps the delegate in a private `DelegatingChatRouteSelector`. Contrast the three
"selector" names:

| Name | What it is | `new`-able? |
|---|---|---|
| `IChatRouteSelector` | the interface (contract) | no (interface) |
| `ChatRouteSelector` | a static factory holding `Create(...)` | no (static) |
| `SemanticChatRouteSelector`, `ComplexityChatRouteSelector` | concrete policies | yes |

So the capability comparison is *not* "`ChatRouteSelector` vs `ComplexityChatRouteSelector`." It's:
a **pre-built appliance** (a concrete selector with domain logic baked in) vs. a **bring-your-own-
function adapter** (`ChatRouteSelector.Create`, whose capability is exactly whatever your lambda
does). Both produce the same `IChatRouteSelector`. `RoutingChatClientBuilder.UseSelector` has
overloads for the interface *and* for raw delegates, so the two paths are interchangeable at the call
site.

### `ChatRouteContext.cs` — the selector's input

The read-only bundle the mechanism hands to every selector call:

- **`Messages`** — the chat being routed (inspect the prompt to decide).
- **`Options`** — the `ChatOptions` (e.g. `ModelId`, `ConversationId`, `Tools`, `Reasoning`). This is
  why "chat options as routing metadata" matters: selectors read these optional fields as routing
  signals.
- **`Models`** — the registered candidate `RoutingChatModel`s you may pick from.

It validates `messages`/`models` non-null in the constructor and is otherwise an immutable
data carrier. When a selector parameter is named `ctx`/`context` in examples, *this* is the type.

### `ChatRoutePlan.cs` — the selector's output (and why it isn't just a model)

A selector does **not** return "the one model." It returns a `ChatRoutePlan`, which encodes two
things a bare `RoutingChatModel` cannot:

1. **An ordered preference chain.** `OrderedModels` is primary-first; the rest are fallbacks the
   router tries in order on failure. A selector with a genuine ranking (the semantic router orders
   by descending similarity) emits a multi-model plan whose tail *is* a meaningful fallback chain.
   A selector that naturally picks one model (a complexity classifier maps a request to exactly one
   tier/model) returns a **one-model plan** and leaves the rest to the router's fallback policy —
   it has no honest ranking of the other models to fabricate. There is a single-model convenience
   constructor for exactly this case.
2. **Optional decision metadata.** `DecisionMetadata` lets a selector attach decision-rationale
   (the complexity tier it classified, the semantic score the winner earned) that the router surfaces
   as `routing.decision` trace tags. A bare model has nowhere to hang that rationale.

Construction validates a non-empty, non-null model list and defensively copies it into a
`ReadOnlyCollection`.

### Fallback: plan tail (policy) + router fallback policy (mechanism)

Fallback is split across the same mechanism/policy line as selection:

- **The plan tail is policy.** When a selector *has* a real ranking, it expresses fallback by
  returning more than one model. The mechanism never reorders the plan; it just walks it.
- **The fallback policy is mechanism.** A one-model plan has no tail, so to give single-model
  selectors resilience the **router** owns an optional fallback policy, configured with
  `RoutingChatClientBuilder.UseFallback()`. After the plan's models are exhausted, the policy
  receives the route context and the registered models the plan omitted, and returns the order in
  which to try them. The parameterless overload uses registration order (zero opinion about
  fitness — it's literally the order you registered); the delegate overload lets the consumer order
  the tail (for example cheapest-first). When no policy is configured, the router only attempts the
  plan's models. This keeps the mechanism opinion-free by default while letting a complexity
  classifier stay out of the fallback business entirely.

---

## The mechanism

### `RoutingChatClient.cs` — the orchestrator

The heart of the feature, and an `IChatClient` itself. Responsibilities, in order of a request:

1. **Normalize** the messages into a re-enumerable list (`NormalizeMessages`) so the selector and the
   invocation see the same sequence without double-enumerating a lazy source.
2. **Apply the capability gate** (`GetCandidateModels`) — narrow the registered models to those that
   can satisfy the capabilities the request *provably* needs (image content ⇒ `Vision`; supplied
   `ChatOptions.Tools` ⇒ `ToolCalling`). The gate is **soft**: if no model declares a required
   capability it returns the full set rather than stranding the request, and `UseCapabilityGate(false)`
   disables it entirely. The resulting candidate set feeds both the `ChatRouteContext` the selector sees
   and the fallback chain. Only high-confidence, request-derived signals are used — fuzzy dimensions
   (e.g. "reasoning") are left to the selector.
3. **Run the selector** (`RunSelectorAsync`) for every request — or, when no selector was supplied, the
   opinion-free `DefaultSelectRoute`: honor `ChatOptions.ModelId` (matched against each model's `ModelId`
   or `Name`), otherwise the first registered model. The router holds no cross-request state; each
   request is routed from scratch.
4. **Build the attempt order** (`BuildAttemptOrder`) — start with `plan.OrderedModels`; if a fallback
   policy was configured (`UseFallback`), append the candidate models the plan omitted, in the order
   the policy returns, de-duped so each candidate is tried at most once.
5. **Invoke with fallback** — walk that attempt order; call `GetResponseAsync` on each model's
   `Client`. On exception, the `catch ... when (i < ordered.Length - 1 && !ct.IsCancellationRequested)`
   guard falls through to the next model; the final model's exception propagates. Cancellation is
   never swallowed.
6. **Forward options carefully** (`CreateForwardedOptions`): the chosen model's provider `ModelId` is
   injected **only** when the caller didn't already pin one, by cloning the options. Routing internals
   are **never** written into the forwarded request — the inner client sees a clean request.
7. **Stamp the result** (`StampResponse`): write the selected model's name/id/provider into the
   response's `AdditionalProperties` (keys `routing.selected_model`, `routing.selected_model_id`,
   `routing.selected_provider`) and tag the current `Activity` for telemetry.

Streaming (`GetStreamingResponseAsync`) mirrors this with one important nuance: **fallback applies
only until the first update is produced.** It pulls the first item inside a try/catch; if that throws,
it disposes the enumerator and tries the next model. Once the first update streams, the model is
committed (you can't un-send bytes), so later failures propagate. Only the first update is stamped
(`StampUpdate`), plus the `Activity`.

Other details:
- **`GetService`** returns `this` for a matching service type, and exposes the selector when asked
  (so middleware can discover the active policy).
- **Trace events.** Alongside the response stamping, the router records two kinds of `ActivityEvent`
  on `Activity.Current` (each a no-op unless the activity is recording): one `routing.decision`
  event per request (selected model + any selector rationale such as the
  complexity tier or semantic score it stamped onto `ChatRoutePlan.DecisionMetadata`), and one
  `routing.attempt` event per model tried (the fallback timeline). It creates no `ActivitySource` of
  its own. Event names are public consts (`RoutingChatClient.DecisionEventName` /
  `AttemptEventName`); tag schemas are documented in [routing-chat-client.md](routing-chat-client.md).
- **`Dispose`** disposes each distinct inner `IChatClient` exactly once (a `HashSet` dedupes the case
  where the same client backs multiple models).
- **Validation**: `ValidateModels` requires ≥1 model, each bound to a client; `ValidateRoutedModel`
  enforces that a selector can only route to a *registered* model (a buggy selector returning a
  stranger throws rather than silently misbehaving).

The class is `public` and non-sealed with a `virtual GetService`/`Dispose(bool)` so consumers can
subclass; the `#pragma warning disable SA1204` at the top is because the static helpers are
intermixed with instance members for readability.

### `RoutingChatClientBuilder.cs` — fluent assembly

A small builder that accumulates models and configuration, then `Build()`s a `RoutingChatClient`:

- `AddModel(name, client, ...)` — add a custom model with inline metadata.
- `AddModel(RoutingChatModel entry, IChatClient client)` / `AddModel(entry)` — add from an existing
  (metadata-only or already-bound) model. Duplicate names are rejected (case-insensitive).
- `AddFromCatalog(name, client)` / `AddFromCatalog(catalog, name, client)` — pull metadata from a
  `RoutingChatModelCatalog` and bind a client.
- `UseSelector(...)` — three overloads: an `IChatRouteSelector`, an async delegate, or a sync delegate
  (the latter two route through `ChatRouteSelector.Create`). Passing `null` selects the opinion-free
  default.
- `UseCapabilityGate(bool)` — toggle the router's soft capability gate (on by default).

It's deliberately thin: all real behavior lives in `RoutingChatClient`.

### `RoutingChatModel.cs` — a candidate's metadata + (optional) client

Describes a model the router can route to. It carries **advisory metadata** — `Name` (stable key),
`ProviderName`, `ModelId`, `Traits`, `MaxInputTokens` (context window), input/output
`...CostPerMillion`, `TypicalLatency`, `SourceUri`/`UpdatedAt` (provenance), free-form
`AdditionalProperties` — plus an **optional** bound `Client`.

Two things make this design work:

- **Metadata-only instances** (no `Client`) are legal. They can live in a `RoutingChatModelCatalog`
  and be bound to a concrete client later via **`WithClient(client)`**, which returns a copy with the
  same metadata. This separates *what we know about a model* (which changes as providers ship models)
  from *how we call it* (the client) — the "model currency" story.
- **The mechanism never interprets the metadata.** Only a selection policy reads `Traits`, `cost`,
  `MaxInputTokens`, etc. The constructor validates non-negative numerics and a non-blank name, and
  clones `AdditionalProperties` defensively.

### `RoutingChatModelCatalog.cs` — name → metadata lookup

A case-insensitive dictionary of metadata-only `RoutingChatModel` entries. `Get`/`TryGet` look up by
name; `CreateModel(name, client)` is sugar for `Get(name).WithClient(client)`. Duplicate names are
rejected at construction. This is the durable store of "models we know about," decoupled from any
client — you refresh it (e.g. from a LiteLLM snapshot) independently of wiring up clients.

### `RoutingChatModelTraits.cs` — objective capability flags only

A `[Flags]` enum of **objective** capabilities that can be read straight from a provider catalog:
`None`, `ToolCalling` (LiteLLM `supports_function_calling`/`supports_tool_choice`), `Vision`
(`supports_vision`/`supports_image_input`), `Reasoning` (`supports_reasoning`).

The deliberate omission matters: **subjective dimensions** (a "quality" score, a "coding" judgement,
"low cost") are **not** modeled here, because they aren't facts you can read from a catalog — they're
opinions that drift. Put those in `RoutingChatModel.AdditionalProperties` instead. This keeps the
enum small, factual, and stable as the AI landscape shifts. Traits are intended to be used as a
**capability gate** — "can this model do what the request needs *at all*?" — and not as a proxy for
quality: most modern chat models share the same flags, so traits cannot rank models on quality.

---

## The shipped policies

### `SemanticChatRouteSelector.cs` (+ `SemanticRouterOptions.cs`, `SemanticRouteAggregation.cs`) — embedding-based routing

A **faithful port of LiteLLM's "auto router" (its semantic router)**, which delegates its routing math
to the `semantic-router` library. Each model is described by a few representative "utterances" (a
profile). At request time it embeds the last user message and routes to the model whose profile
utterances are most cosine-similar, using the same algorithm LiteLLM does.

- Profiles are embedded **once**, lazily, and cached (`EnsureProfilesAsync` + a double-checked lock on
  `_profilesTask`) into a flat index (each utterance vector tagged with its model). A faulted/canceled
  embedding task is **not** cached, so the next call retries.
- Per request: embed the last **user** message (no fallback to other roles, matching LiteLLM's
  `_extract_text_from_messages`); score every utterance with `TensorPrimitives.CosineSimilarity`; keep
  the globally highest **`TopK`** matches (default `5`); group those by model; combine each model's
  matched scores with **`Aggregation`** (`Mean` by default, or `Sum`/`Max`).
- **Threshold gate.** In descending aggregated-score order, route to the first model whose score meets
  its threshold — a per-model override (`ScoreThresholdByModel`, mirroring `semantic-router`'s per-route
  `score_threshold`) if present, else the global **`ScoreThreshold`** (default `0.3`, LiteLLM's default
  encoder threshold). If none pass — or the request has no user text — the optional **`defaultModel`**
  (else the first registered model) becomes the primary route.
- The plan ranks all models: primary first, then the rest by descending aggregated score. Wrap the
  injected `IEmbeddingGenerator` with caching to amortize the per-request query embedding.

This is the heavier, "understands intent" policy — appropriate when keyword rules are too brittle.
Tune it with `SemanticRouterOptions` (`TopK`, `Aggregation`, `ScoreThreshold`, `ScoreThresholdByModel`).

### `ComplexityChatRouteSelector.cs` (+ `ComplexityRouterOptions.cs`, `ChatComplexityTier.cs`)

A **faithful port of LiteLLM's complexity router**. It scores each request with deterministic,
sub-millisecond keyword/pattern rules, classifies it into a `ChatComplexityTier`, and routes to the
model the caller explicitly mapped to that tier.

**`ChatComplexityTier.cs`** is the four-value enum: `Simple`, `Medium`, `Complex`, `Reasoning`
(mirroring LiteLLM's `SIMPLE`/`MEDIUM`/`COMPLEX`/`REASONING`).

**`ComplexityRouterOptions.cs`** is the entire configurable surface — seven dimension weights, three
tier boundaries, the short/long token thresholds, the reasoning-override count, and the keyword lists
(code/reasoning/technical/simple) and multi-step **regexes**. Every default mirrors LiteLLM's
`config.py`. Because the lists are just data, swapping `TechnicalTerms` for your own vocabulary turns
this into a domain-aware router for free.

**`ComplexityChatRouteSelector.cs`** is the scorer. The constructor validates the tier map and
precompiles the multi-step patterns into `Regex[]` (case-insensitive, culture-invariant, with a
100 ms match timeout as a ReDoS guard). `SelectRouteAsync`:

- Extracts the **last** user message and **last** system message (`ExtractUserAndSystem`).
- With **no user message**, faithfully prefers the explicit `defaultModel`, else the `Medium`-tier
  model (there's nothing to score).
- Otherwise calls `Classify`, maps the tier to a model name (falling back to `defaultModel` for an
  unmapped tier, and to the first registered model when the mapped name is not registered), and
  returns a **single-model plan** for that model (`SelectTarget`). A tier classifier picks exactly one
  model and has no meaningful ranking of the others, so it leaves fallback to the router's
  `UseFallback` policy.

`Classify` computes LiteLLM's exact weighted sum across the seven dimensions, applies the
"2+ distinct reasoning markers ⇒ `Reasoning`" override, then maps the score against the three
boundaries. The matching helpers are faithful too: single-word keywords match on **word boundaries**
(`ContainsWord`/`IsWordChar`, so `api` ≠ `capital`); multi-word phrases match as substrings; bucket
scores are discrete, not linear; code/technical/simple read system+user text while reasoning markers,
token count, and question count read the user message only. (The full scoring table, formula, and a
worked example are in [`routing-chat-client.md`](./routing-chat-client.md#how-litellms-complexity-router-works-and-how-this-port-mirrors-it).)

`ClassifyTier(messages)` is public so callers can get the tier without routing (no-user ⇒ `Medium`).

### `ChatModelCatalog.cs` (+ `ChatModelCatalogOptions.cs`) — the catalog adapter

A **policy-side helper** that maps external catalog documents into metadata-only `RoutingChatModel`
entries. It is *not* part of the mechanism; it just produces advisory metadata a selector can consume.
Two sources are supported via paired parse/load methods:

- `ParseLiteLlm(json)` / `LoadLiteLlm(stream)` — LiteLLM's `model_prices_and_context_window.json`
  (a name-keyed object); skips the `sample_spec` pseudo-entry and entries whose `mode != "chat"`.
- `ParseGitHubModels(json)` / `LoadGitHubModels(stream)` — the GitHub Models catalog at
  `https://models.github.ai/catalog/models` (a JSON array); skips embedding-only entries.

Both produce models de-duplicated by name (case-insensitive, first wins). **Only objective fields are
mapped.** Capability flags become `RoutingChatModelTraits` (LiteLLM `supports_*`; GitHub Models
`capabilities` plus `supported_input_modalities` for `Vision`); `max_input_tokens` becomes
`MaxInputTokens`. LiteLLM additionally carries per-token cost (converted to per-million) — the GitHub
Models catalog has **no pricing**, which is exactly why the two are complementary: GitHub Models supplies
capabilities and context limits, LiteLLM adds cost. Source-specific extras are carried under
`AdditionalProperties` keyed with the `litellm.` or `github.` prefix. The GitHub Models parser resolves
each entry's publisher-prefixed `id` (e.g. `openai/gpt-5-mini`) to a bare `Name` (`gpt-5-mini`, the merge
key shared with LiteLLM) while keeping the full id as `ModelId` (what the inference endpoint expects).
**Latency and quality are never inferred** — the catalogs carry no such data, so those stay the
selector's responsibility.

**`ChatModelCatalogOptions.cs`** controls both mappings: `ChatModelsOnly` (default `true`; LiteLLM keeps
`mode == "chat"`, GitHub Models keeps entries whose output modalities include `"text"`), `UpdatedAt`
(provenance stamp recorded on each model so a policy can reason about staleness), `SourceUri` (fallback
source), and `IncludeModel` (a bare-name predicate to include/exclude entries). Produced models carry
**no client** — bind one with `WithClient` or via a `RoutingChatModelCatalog`.

---

## The tests

All three test files live under
`test/Libraries/Microsoft.Extensions.AI.Tests/ChatRouting/` and run on `net10.0`.

### `RoutingChatClientTests.cs` — the mechanism (and selector integration)

The broadest suite. It exercises the orchestrator end-to-end with fake `IChatClient`s and confirms
each mechanism guarantee:

- **Construction/validation:** rejects empty model lists and models without a client; the builder
  rejects duplicate model names.
- **Default selector:** forwards the model id, stamps the response, and crucially **does not leak**
  routing internals into the forwarded request; honors an explicit `ChatOptions.ModelId`.
- **Fallback:** walks the plan on failure and stamps the model that actually succeeded; propagates the
  last exception when the chain is exhausted; throws when a selector routes to an unregistered model.
- **Streaming:** stamps only the first update; falls back before the first update is produced.
- **Plumbing:** `GetService` returns both the client and the selector; `Dispose` disposes each client
  exactly once (verified with a `CountingDisposeClient`).
- **Capability gate (integration):** an image request narrows to a `Vision` model and a tools request
  narrows to a `ToolCalling` model (even under the opinion-free default selector); the gate falls
  through to the full set when no model declares the capability; `UseCapabilityGate(false)` bypasses it
  and routes to the otherwise-ineligible primary.
- **Shipped selectors (integration):** `SemanticChatRouteSelector`
  routes to the most similar model, aggregates a model's matches by mean (or sum/max), applies the
  global top-k, honors per-model thresholds, and falls back to the default model when nothing passes
  the threshold or the request has no user text.

### `ComplexityChatRouteSelectorTests.cs` — the complexity port

Validates the faithful scoring and the routing wrapper: constructor rejects null/empty tier maps;
`ClassifyTier` classifies a greeting as `Simple`, forces `Reasoning` on two reasoning markers, and
rates a code+technical prompt as `Complex`; `SelectRouteAsync` routes to the tier-mapped model, falls
back to the default model for an unmapped tier, and falls back to the first registered model when the
target model isn't present; and custom keyword lists change the classification (proving the options are honored).

### `ChatModelCatalogTests.cs` — the catalog adapter

Validates both mappings. For the LiteLLM parser: rejects null JSON; skips `sample_spec` and non-chat
entries by default; maps the capability `supports_*` flags to traits; **never infers latency or
quality**; converts per-token cost to per-million; carries the curated objective metadata under the
`litellm.` prefix; leaves `MaxInputTokens` null when absent; includes embeddings when
`ChatModelsOnly == false`; applies the `IncludeModel` filter and the `UpdatedAt` provenance;
de-duplicates by name case-insensitively; `LoadLiteLlm(stream)` matches `ParseLiteLlm(string)`; and a
parsed entry binds cleanly into a catalog and a client. For the GitHub Models parser: skips
embedding-only entries by default; strips the publisher prefix for `Name` while keeping the full
publisher-prefixed `ModelId`; maps `capabilities` and `supported_input_modalities` to traits; **never
infers cost or latency**; carries `github.`-prefixed metadata; applies `ChatModelsOnly`, `IncludeModel`,
and stream parity; and rejects a non-array root. A final cross-catalog test proves a shared model
(`gpt-5-mini`) resolves to the same merge `Name` across both sources.

---

## Mental model, in one line

**The mechanism is dumb and stable; the policy is smart and replaceable.** `RoutingChatClient` gives
you composition (nesting + middleware), fallback, and response stamping for free;
`IChatRouteSelector` supplies the judgment — whether that's the empty default, one of the shipped
experimental selectors, or your own lambda. `ChatRoutePlan` exists because a routing decision is
*ordered preference*, not just a single model; and when a selector picks one
model, the router's own `UseFallback` policy owns the order in which the remaining models are tried.
