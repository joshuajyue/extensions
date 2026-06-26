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
| **Mechanism** | `RoutingChatClient`, `RoutingChatClientBuilder`, `RoutingStickiness`, `RoutingChatModel`, `RoutingChatModelCatalog`, `RoutingChatModelTraits` |
| **Policy contract** | `IChatRouteSelector`, `ChatRouteSelector`, `ChatRouteContext`, `ChatRoutePlan`, `ChatRouteAttempt` |
| **Shipped policies & adapters** | `RuleBasedChatRouteSelector`, `SemanticChatRouteSelector`, `ComplexityChatRouteSelector` (+ `ComplexityRouterOptions`, `ChatComplexityTier`), `LiteLlmModelCatalog` (+ `LiteLlmCatalogOptions`) |

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
| `RuleBasedChatRouteSelector`, `SemanticChatRouteSelector`, `ComplexityChatRouteSelector` | concrete policies | yes |

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
- **`PreviousAttempt`** — a `ChatRouteAttempt?`, `null` on the initial selection. The hook for
  failure-adaptive policies (see below).

It validates `messages`/`models` non-null in the constructor and is otherwise an immutable
data carrier. When a selector parameter is named `ctx`/`context` in examples, *this* is the type.

### `ChatRoutePlan.cs` — the selector's output (and why it isn't just a model)

A selector does **not** return "the one model." It returns a `ChatRoutePlan`, which encodes two
things a bare `RoutingChatModel` cannot:

1. **An ordered fallback chain.** `OrderedModels` is primary-first; the rest are fallbacks tried in
   order on failure. If the contract returned a single model, fallback would have to become a
   *mechanism* opinion — violating the mechanism-is-opinion-free rule. The plan is how the **policy**
   owns the fallback order.
2. **A self-invalidation rule.** The optional `RemainsValid` predicate
   (`Func<ChatRouteContext, CancellationToken, ValueTask<bool>>`) lets a *cached* sticky decision
   decide it is stale. This is the policy-side complement to `RoutingStickiness` (the mechanism-side
   caching scope). Example: "stick to this model for the conversation **unless** complexity just
   jumped." A static return value has nowhere to hang that rule.

Construction validates a non-empty, non-null model list and defensively copies it into a
`ReadOnlyCollection`. There's a single-model convenience constructor for the common case.

**Plan vs. PreviousAttempt — proactive vs. reactive fallback.** These are not two ways to do the same
thing; they are two *timings*:

- `ChatRoutePlan` decides the **whole fallback chain up front, failure-blind**. The selector runs
  once and never learns *how* a model failed. Use it when the preference order is known in advance
  ("smart → cheap → local, retry on any error").
- `PreviousAttempt` decides **one model at a time, reacting to how the last attempt failed** (status
  code, exception type). Use it for circuit breakers, error-type branching, or cost-aware escalation
  ("only pay for the expensive model *if* the cheap one actually refused"). A plan can't express
  "if it's a 429 go here, if it's a content filter go there," because the list is fixed before any
  exception exists.

### `ChatRouteAttempt.cs` — the reactive hook (reserved)

A record of **one try**: the `Model`, the 1-based `AttemptNumber`, and the `Exception` (if it
failed). It is surfaced to the policy through `ChatRouteContext.PreviousAttempt` so an adaptive
selector can react to a specific failure.

**Honest status:** this is a *plumbed-but-not-yet-driven* extension point. The mechanism's current
fallback loop walks `plan.OrderedModels` in place and always builds the context with
`previousAttempt = null` — it never re-invokes the selector mid-request with a populated
`PreviousAttempt`. The type and the context property are public and stable, but no shipped code feeds
them yet. They exist as the seam for a future milestone: re-running selection on model
failure/unavailability (the "circuit breaking / fallback on unavailable model" idea). Shipping the
seam now means that milestone won't be a breaking API change.

---

## The mechanism

### `RoutingChatClient.cs` — the orchestrator

The heart of the feature, and an `IChatClient` itself. Responsibilities, in order of a request:

1. **Normalize** the messages into a re-enumerable list (`NormalizeMessages`) so the selector and the
   invocation see the same sequence without double-enumerating a lazy source.
2. **Get a plan** (`GetPlanAsync`), honoring the configured `RoutingStickiness`:
   - `EveryCall` → run the selector every time.
   - `PerInstance` → run once, cache on the instance (`_instancePlan`, guarded with
     `Volatile.Read/Write`), reuse for all later requests.
   - `ByConversationId` → cache per `ChatOptions.ConversationId` in a `ConcurrentDictionary`; with no
     conversation id, transparently behave like `EveryCall`.
   Before reusing any cached plan it awaits `PlanRemainsValidAsync`, which calls the plan's
   `RemainsValid` predicate (if any) and re-runs the selector when it returns `false`.
3. **Run the selector** (`RunSelectorAsync`) — or, when no selector was supplied, the opinion-free
   `DefaultSelectRoute`: honor `ChatOptions.ModelId` (matched against each model's `ModelId` or
   `Name`), otherwise the first registered model.
4. **Invoke with fallback** — walk `plan.OrderedModels`; call `GetResponseAsync` on each model's
   `Client`. On exception, the `catch ... when (i < ordered.Count - 1 && !ct.IsCancellationRequested)`
   guard falls through to the next model; the final model's exception propagates. Cancellation is
   never swallowed.
5. **Forward options carefully** (`CreateForwardedOptions`): the chosen model's provider `ModelId` is
   injected **only** when the caller didn't already pin one, by cloning the options. Routing internals
   are **never** written into the forwarded request — the inner client sees a clean request.
6. **Stamp the result** (`StampResponse`): write the selected model's name/id/provider into the
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
- `UseStickiness(RoutingStickiness)` — set the caching scope.

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
enum small, factual, and stable as the AI landscape shifts.

### `RoutingStickiness.cs` — the caching *scope*

An enum: `EveryCall` (re-decide every request), `PerInstance` (decide once per client instance),
`ByConversationId` (decide once per `ConversationId`, falling back to `EveryCall` when absent).

Crucially, stickiness is **only the "when do I reuse a cached decision?" half**. The complementary
"when is a cached decision no longer valid?" half belongs to the *policy* and is expressed by
`ChatRoutePlan.RemainsValid`. Together they let you say "stick per conversation, but re-route if the
selector's own validity rule says the situation changed."

---

## The shipped policies

### `RuleBasedChatRouteSelector.cs` — deterministic, metadata-driven ranking

A stateless (singleton `Instance`) policy that **ranks all models** and returns them as a plan
(best-ranked primary, the rest fallbacks). The comparison (`CompareModels`) is a strict priority
cascade:

1. **Context fit (hard filter).** A model whose `MaxInputTokens` cannot hold the (roughly estimated,
   ~4 chars/token) prompt is ranked *below* every fitting model — a too-small window is guaranteed to
   fail, so it's the strongest signal. Unknown context window = assumed to fit (can't prove it won't).
2. **Required traits.** Traits are *inferred from the request*: `options.Tools` ⇒ `ToolCalling`,
   `options.Reasoning` ⇒ `Reasoning`. Models that satisfy all required traits rank above those that
   don't. (Only these two objective inferences are made — nothing subjective.)
3. **Cost** (lower total input+output `...CostPerMillion` is better).
4. **Latency** (lower `TypicalLatency` is better).

Ties preserve registration order (the sort is stable, and unknown values sort consistently via the
`CompareLowerIsBetter` helpers). This is the "sensible default ranking" policy — useful out of the
box, easy to reason about, no I/O.

### `SemanticChatRouteSelector.cs` — embedding-based routing

Routes by **meaning**. Each model is described by a few representative "utterances" (a profile). At
request time it embeds the last user message and routes to the model whose profile utterances are
most cosine-similar.

- Profiles are embedded **once**, lazily, and cached (`EnsureProfilesAsync` + a double-checked lock on
  `_profilesTask`). A faulted/canceled embedding task is **not** cached, so the next call retries.
- Similarity uses `TensorPrimitives.CosineSimilarity`. Each model scores the **max** similarity across
  its utterances; models are ranked by score (ties keep registration order) and returned as a plan.
- A `minimumSimilarity` floor guards against forcing a bad match: if the best score is below it (or
  the request has no user text), the **first registered model** becomes the primary route.
- Wrap the injected `IEmbeddingGenerator` with caching to amortize the per-request query embedding.

This is the heavier, "understands intent" policy — appropriate when keyword rules are too brittle.

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
  unmapped tier), and returns a plan with that model primary and the rest as fallbacks in registration
  order (`OrderModels`).

`Classify` computes LiteLLM's exact weighted sum across the seven dimensions, applies the
"2+ distinct reasoning markers ⇒ `Reasoning`" override, then maps the score against the three
boundaries. The matching helpers are faithful too: single-word keywords match on **word boundaries**
(`ContainsWord`/`IsWordChar`, so `api` ≠ `capital`); multi-word phrases match as substrings; bucket
scores are discrete, not linear; code/technical/simple read system+user text while reasoning markers,
token count, and question count read the user message only. (The full scoring table, formula, and a
worked example are in [`routing-chat-client.md`](./routing-chat-client.md#how-litellms-complexity-router-works-and-how-this-port-mirrors-it).)

`ClassifyTier(messages)` is public so callers can get the tier without routing (no-user ⇒ `Medium`).

### `LiteLlmModelCatalog.cs` (+ `LiteLlmCatalogOptions.cs`) — the catalog adapter

A **policy-side helper** that maps LiteLLM's `model_prices_and_context_window.json` document into
metadata-only `RoutingChatModel` entries. It is *not* part of the mechanism; it just produces advisory
metadata a selector can consume.

- `Parse(json)` / `Load(stream)` read the document and produce models, de-duplicated by name
  (case-insensitive, first wins), skipping the `sample_spec` pseudo-entry.
- **Only objective fields are mapped.** `supports_*` flags become `RoutingChatModelTraits`; per-token
  cost is converted to per-million; `max_input_tokens` becomes `MaxInputTokens`; the provider, source,
  and a curated set of additional objective fields (max output tokens, mode, deprecation date, and
  extra capability flags) are carried under `AdditionalProperties` keyed with the `litellm.` prefix.
- **Latency and quality are never inferred** — the catalog carries no such data, so those stay the
  selector's responsibility.

**`LiteLlmCatalogOptions.cs`** controls the mapping: `ChatModelsOnly` (default `true`, keep only
`mode == "chat"`), `UpdatedAt` (provenance stamp recorded on each model so a policy can reason about
staleness), `SourceUri` (fallback source), and `IncludeModel` (a name predicate to include/exclude
entries). Produced models carry **no client** — bind one with `WithClient` or via a
`RoutingChatModelCatalog`.

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
- **Stickiness:** `PerInstance` reuses the first selected model; `ByConversationId` sticks per
  conversation and falls back to `EveryCall` without a conversation id; `RemainsValid` causes a sticky
  hit to be reused *and* re-selected once invalidated (the policy-side invalidation half).
- **Fallback:** walks the plan on failure and stamps the model that actually succeeded; propagates the
  last exception when the chain is exhausted; throws when a selector routes to an unregistered model.
- **Streaming:** stamps only the first update; falls back before the first update is produced.
- **Plumbing:** `GetService` returns both the client and the selector; `Dispose` disposes each client
  exactly once (verified with a `CountingDisposeClient`).
- **Shipped selectors (integration):** `RuleBasedChatRouteSelector` prefers lower cost with no signal,
  and prefers a tool-calling model when tools are requested; `SemanticChatRouteSelector` routes to the
  most similar model and falls back to the first model below the minimum-similarity floor.

### `ComplexityChatRouteSelectorTests.cs` — the complexity port

Validates the faithful scoring and the routing wrapper: constructor rejects null/empty tier maps;
`ClassifyTier` classifies a greeting as `Simple`, forces `Reasoning` on two reasoning markers, and
rates a code+technical prompt as `Complex`; `SelectRouteAsync` routes to the tier-mapped model, falls
back to the default model for an unmapped tier, and preserves registration order when the target model
isn't present; and custom keyword lists change the classification (proving the options are honored).

### `LiteLlmModelCatalogTests.cs` — the catalog adapter

Validates the mapping rules: rejects null JSON; skips `sample_spec` and non-chat entries by default;
maps the capability `supports_*` flags to traits; **never infers latency or quality**; converts
per-token cost to per-million; carries the curated objective metadata under the `litellm.` prefix;
leaves `MaxInputTokens` null when absent; includes embeddings when `ChatModelsOnly == false`; applies
the `IncludeModel` filter and the `UpdatedAt` provenance; de-duplicates by name case-insensitively;
`Load(stream)` matches `Parse(string)`; and a parsed entry binds cleanly into a catalog and a client.

---

## Mental model, in one line

**The mechanism is dumb and stable; the policy is smart and replaceable.** `RoutingChatClient` gives
you composition (nesting + middleware), fallback, stickiness, and response stamping for free;
`IChatRouteSelector` supplies the judgment — whether that's the empty default, one of the shipped
experimental selectors, or your own lambda. `ChatRoutePlan` exists because a routing decision is
*ordered fallbacks + an invalidation rule*, not a single model; `ChatRouteAttempt`/`PreviousAttempt`
are the reserved seam for future failure-adaptive policies.
