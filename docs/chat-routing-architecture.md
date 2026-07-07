# Chat Routing — architecture deep dive

This document explains the `ChatRouting` feature (namespace `Microsoft.Extensions.AI`) **file by file**.
The types live in three homes: the policy seam and model metadata types are part of the core
`Microsoft.Extensions.AI.Abstractions` package; the mechanism
(`RoutingChatClient`) is part of the core `Microsoft.Extensions.AI`
package; and the in-house concrete selectors (`ComplexityChatRouteSelector`,
`SemanticChatRouteSelector`) ship separately in the experimental `Microsoft.Extensions.AI.Routing`
package. The dependency arrow points one way: the selectors package references the core abstractions,
never the other way around.
It is a companion to [`routing-chat-client.md`](./routing-chat-client.md) (which is the user-facing
how-to) and [`semantic-chat-client-selection.md`](./semantic-chat-client-selection.md). Here the goal
is to explain *why each type exists* and *how the pieces fit together*, in enough depth that a new
contributor can reason about — and safely extend — the design.

> All public types in this folder are marked `[Experimental(AIRoutingChat)]` (diagnostic id
> `MEAI001`). The shape may change before it stabilizes.

Algorithm attribution: the complexity selector follows ClawRouter's approach, and the semantic
selector implements Aurelio Labs' `semantic-router` algorithm. Both are MIT-licensed; see
[`THIRD-PARTY-NOTICES.TXT`](../THIRD-PARTY-NOTICES.TXT).

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
| **Mechanism** | `RoutingChatClient`, `ChatRoute`, `ChatRouteCatalog`, `ChatModelCapabilities` |
| **Policy contract** | `IChatRouteSelector`, `ChatRouteSelector`, `ChatRouteContext`, `ChatRoutePlan` |
| **Shipped policies** | `SemanticChatRouteSelector` (+ `SemanticRouterOptions`, `SemanticRouteAggregation`), `ComplexityChatRouteSelector` (+ `ComplexityRouterOptions`, `ChatComplexityTier`) |

---

## The policy contract

*Part of the core `Microsoft.Extensions.AI.Abstractions` package.*

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
does). Both produce the same `IChatRouteSelector`. The router's `selector` constructor parameter
accepts an `IChatRouteSelector`; wrap a raw delegate with `ChatRouteSelector.Create` first, so the
two paths are interchangeable at the call site.

### `ChatRouteContext.cs` — the selector's input

The read-only bundle the mechanism hands to every selector call:

- **`Messages`** — the chat being routed (inspect the prompt to decide).
- **`Options`** — the `ChatOptions` (e.g. `ModelId`, `ConversationId`, `Tools`, `Reasoning`). This is
  why "chat options as routing metadata" matters: selectors read these optional fields as routing
  signals.
- **`Routes`** — the registered candidate `ChatRoute`s you may pick from.

It validates `messages`/`routes` non-null in the constructor and is otherwise an immutable
data carrier. When a selector parameter is named `ctx`/`context` in examples, *this* is the type.

### `ChatRoutePlan.cs` — the selector's output (and why it isn't just a model)

A selector does **not** return "the one model." It returns a `ChatRoutePlan`, which encodes two
things a bare `ChatRoute` cannot:

1. **An ordered preference chain.** `OrderedRoutes` is primary-first; the rest are fallbacks the
   router tries in order on failure. A selector with a genuine ranking (the semantic router orders
   by descending similarity) emits a multi-route plan whose tail *is* a meaningful fallback chain.
   A selector that naturally picks one route (a complexity classifier maps a request to exactly one
   tier/route) returns a **one-route plan** and leaves the rest to the router's `onFailure` delegate —
   it has no honest ranking of the other routes to fabricate. There is a single-route convenience
   constructor for exactly this case.
2. **Optional decision metadata.** `DecisionMetadata` lets a selector attach decision-rationale
   (the complexity tier it classified, the semantic score the winner earned) that the router surfaces
   as `routing.decision` trace tags. A bare model has nowhere to hang that rationale.

Construction validates a non-empty, non-null route list and defensively copies it into a
`ReadOnlyCollection`.

### Fallback: plan tail (policy) + router `onFailure` delegate (mechanism)

Fallback is split across the same mechanism/policy line as selection:

- **The plan tail is policy.** When a selector *has* a real ranking, it expresses fallback by
  returning more than one route. The mechanism never reorders the plan; it just walks it.
- **The `onFailure` delegate is mechanism.** A one-route plan has no tail, so to give single-route
  selectors resilience the **router** owns an optional `onFailure` delegate. It fires on **each**
  pre-commit failure with a `RouteFailureContext` — the failed route, the unclassified exception, the
  attempt number, and `Remaining` (the still-untried candidates the router can reach: the plan's tail
  plus the registered routes the plan omitted) — and returns the routes to try next, in order.
  Returning `Remaining` unchanged uses that default order (zero opinion about fitness); returning a
  subset prunes (for example, drop every route sharing the failed provider on a 401), a permutation
  reorders the tail (cheapest-first), and returning `null`/empty stops and rethrows. Returned routes
  are de-duped against those already attempted, so routing always terminates. When `onFailure` is
  `null`, the router only attempts the plan's routes. This keeps the mechanism opinion-free by default
  while letting a complexity classifier stay out of the fallback business entirely.

---

## The mechanism

*Part of the core `Microsoft.Extensions.AI` package (`RoutingChatClient`).
The model-metadata types that follow — `ChatRoute`, `ChatRouteCatalog`,
`ChatModelCapabilities` — are the router's input vocabulary and therefore live alongside the policy
seam in `Microsoft.Extensions.AI.Abstractions`.*

### `RoutingChatClient.cs` — the orchestrator

The heart of the feature, and an `IChatClient` itself. Responsibilities, in order of a request:

1. **Normalize** the messages into a re-enumerable list (`NormalizeMessages`) so the selector and the
   invocation see the same sequence without double-enumerating a lazy source.
2. **Apply the capability gate** (`GetCandidateRoutes`) — ask the `capabilityDetector` delegate which
   open string tokens the request *provably* needs, then narrow the registered routes to those whose
   declared tokens (under `ChatModelCapabilities.PropertyKey` in `AdditionalProperties`) are a
   superset. The default detector emits `ChatModelCapabilities.Vision` for image content and
   `ChatModelCapabilities.FunctionCalling` for `AIFunctionDeclaration` tools. The gate is **soft**:
   if no route declares a required capability it returns the full set rather than stranding the
   request, and a detector that always returns no tokens disables it entirely. The resulting candidate
   set feeds both the `ChatRouteContext` the selector sees and the `onFailure` lookahead
   (`RouteFailureContext.Remaining`). Only high-confidence,
   request-derived signals are used — fuzzy dimensions (e.g. "reasoning") are left to the selector.
3. **Run the selector** (`RunSelectorAsync`) for every request — or, when no selector was supplied, the
   opinion-free `DefaultSelectRoute`: honor `ChatOptions.ModelId` (matched case-insensitively against
   each model's `ModelId` or `Name`), otherwise the first registered model. The router holds no
   cross-request state; each request is routed from scratch.
4. **Seed the attempt queue** (`DedupPlan`) — start with `plan.OrderedRoutes`, de-duped so each route
   is attempted at most once.
5. **Invoke reactively** — walk the queue, calling `GetResponseAsync` on each route's `Client`. On a
   pre-commit exception, `NextAfterFailure` consults the `onFailure` delegate (or, when none was
   supplied, walks the plan's remaining routes) and returns the next routes to try; the router
   replaces the queue with that set and continues. A `null`/empty result — or an exhausted default
   walk — rethrows the last exception. A growing `tried` set makes each route attempted at most once,
   so the loop always terminates. Cancellation is never treated as a failure and never reaches the
   delegate.
6. **Forward options carefully** (`CreateForwardedOptions`): the chosen model's provider `ModelId` is
   injected **only** when the caller didn't already pin one, by cloning the options. Routing internals
   are **never** written into the forwarded request — the inner client sees a clean request.
7. **Stamp the result** (`StampResponse`): write the selected model's name/id/provider into the
   response's `AdditionalProperties` (keys `routing.selected_route`, `routing.selected_model_id`,
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
- **Validation**: `ValidateRoutes` requires ≥1 route, each bound to a client; `ValidateRoutedRoute`
  enforces that a selector can only route to a *registered* route (a buggy selector returning a
  stranger throws rather than silently misbehaving).

The class is `public` and non-sealed with a `virtual GetService`/`Dispose(bool)` so consumers can
subclass; the `#pragma warning disable SA1204` at the top is because the static helpers are
intermixed with instance members for readability.

### Construction — a plain public constructor

There is no routing-specific builder: `RoutingChatClient` is constructed directly through its
public constructor and composes with the existing `AddChatClient` / `ChatClientBuilder`
infrastructure like any other `IChatClient`. The constructor takes:

- `routes` — the candidate `ChatRoute`s, each bound to an `IChatClient`. It validates the
  list is non-empty, that every route carries a client, and that names are unique (case-insensitive).
- `selector` — the selection policy, or `null` for the opinion-free default. Wrap a raw delegate
  with `ChatRouteSelector.Create` to pass a lambda here.
- `onFailure` — the optional per-failure delegate (see [Fallback](#fallback) above); `null`
  attempts only the plan's routes.
- `capabilityDetector` — optional delegate returning the capability tokens a request provably
  requires. `null` uses the default detector; `static (_, _) => Array.Empty<string>()` disables the
  gate by making every route a candidate.

All real behavior lives in `RoutingChatClient` itself; there is no separate assembly step. Bind
catalog metadata to a client with `ChatRoute.WithClient(client)` (or
`ChatRouteCatalog.CreateRoute(name, client)`) when composing the `routes` list.

### `ChatRoute.cs` — a candidate's metadata + (optional) client

Describes a route the router can dispatch to. It carries **advisory metadata** — `Name` (stable key),
`ProviderName`, `ModelId`, `MaxInputTokens` (context window), input/output
`...CostPerMillion`, `TypicalLatency`, `SourceUri`/`UpdatedAt` (provenance), free-form
`AdditionalProperties` — plus an **optional** bound `Client`.

Two things make this design work:

- **Metadata-only instances** (no `Client`) are legal. They can live in a `ChatRouteCatalog`
  and be bound to a concrete client later via **`WithClient(client)`**, which returns a copy with the
  same metadata. This separates *what we know about a model* (which changes as providers ship models)
  from *how we call it* (the client) — the "model currency" story.
- **The mechanism mostly treats metadata as advisory.** The capability gate reads only the open
  capability tokens declared under `ChatModelCapabilities.PropertyKey`; selectors decide how to use
  `cost`, `MaxInputTokens`, subjective app-specific properties, and any other metadata. The
  constructor validates non-negative numerics and a non-blank name, and clones
  `AdditionalProperties` defensively.

### `ChatRouteCatalog.cs` — name → metadata lookup

A case-insensitive dictionary of metadata-only `ChatRoute` entries. `Get`/`TryGet` look up by
name; `CreateRoute(name, client)` is sugar for `Get(name).WithClient(client)`. Duplicate names are
rejected at construction. This is the durable store of "models we know about," decoupled from any
client — you refresh it (e.g. from a provider catalog snapshot) independently of wiring up clients.

### `ChatModelCapabilities.cs` — objective capability tokens

A static class of well-known **open string tokens** and the `ChatRoute.AdditionalProperties` key used
to declare them. A route stores an `IEnumerable<string>` under `ChatModelCapabilities.PropertyKey`
(`"capabilities"`); the router compares that declared set with the tokens a request requires.

The well-known tokens are `Vision` (`"vision"`), `FunctionCalling` (`"function_calling"`),
`ResponseSchema` (`"response_schema"`), `PdfInput` (`"pdf_input"`), `AudioInput`
(`"audio_input"`), `Reasoning` (`"reasoning"`), and `WebSearch` (`"web_search"`), named to align
with LiteLLM's `supports_*` flags. The vocabulary is intentionally open: an app can add a bespoke
token such as `"legal_reviewed"` with no library change.

The deliberate omission still matters: **subjective dimensions** (a "quality" score, a "coding"
judgement, "low cost") are **not** well-known capability tokens, because they aren't objective facts
you can read from a catalog — they're opinions that drift. Put those in
`ChatRoute.AdditionalProperties` under app-specific keys and have selectors read them. Capability
tokens are for the correctness gate — "can this route do what the request needs *at all*?" — and not
as a proxy for quality: most modern chat models share many capabilities, so tokens cannot rank models
on quality.

---

## The shipped policies

*The experimental `Microsoft.Extensions.AI.Routing` package — the in-house selector sandbox.*

### `SemanticChatRouteSelector.cs` (+ `SemanticRouterOptions.cs`, `SemanticRouteAggregation.cs`) — embedding-based routing

A .NET implementation of Aurelio Labs' `semantic-router` routing algorithm (the same library
LiteLLM's "auto router" delegates to). Each model is described by a few representative "utterances"
(a profile). At request time it embeds the last user message and routes to the model whose profile
utterances are most cosine-similar.

- Profiles are embedded **once**, lazily, and cached (`EnsureProfilesAsync` + a double-checked lock on
  `_profilesTask`) into a flat index (each utterance vector tagged with its model). A faulted/canceled
  embedding task is **not** cached, so the next call retries.
- Per request: embed the last **user** message (no fallback to other roles, matching the semantic
  router's input scope); score every utterance with `TensorPrimitives.CosineSimilarity`; keep
  the globally highest **`TopK`** matches (default `5`); group those by route; combine each route's
  matched scores with **`Aggregation`** (`Mean` by default, or `Sum`/`Max`).
- **Threshold gate.** In descending aggregated-score order, route to the first route whose score meets
  its threshold — a per-route override (`ScoreThresholdByRoute`, mirroring `semantic-router`'s per-route
  `score_threshold`) if present, else the global **`ScoreThreshold`** (default `0.3`, matching the
  LiteLLM integration default). If none pass — or the request has no user text — the optional
  **`defaultRoute`** (else the first registered route) becomes the primary route.
- The plan ranks all routes: primary first, then the rest by descending aggregated score. Wrap the
  injected `IEmbeddingGenerator` with caching to amortize the per-request query embedding.

This is the heavier, "understands intent" policy — appropriate when keyword rules are too brittle.
Tune it with `SemanticRouterOptions` (`TopK`, `Aggregation`, `ScoreThreshold`, `ScoreThresholdByRoute`).

### `ComplexityChatRouteSelector.cs` (+ `ComplexityRouterOptions.cs`, `ChatComplexityTier.cs`)

Follows ClawRouter's rule-based complexity-scoring approach (which LiteLLM's complexity router is
also based on). It scores each request with deterministic, sub-millisecond keyword/pattern rules,
classifies it into a `ChatComplexityTier`, and routes to the route the caller explicitly mapped to
that tier.

**`ChatComplexityTier.cs`** is the four-value enum: `Simple`, `Medium`, `Complex`, `Reasoning`
(matching the ClawRouter-style tiers that LiteLLM also uses).

**`ComplexityRouterOptions.cs`** is the entire configurable surface — seven dimension weights, three
tier boundaries, the short/long token thresholds, the reasoning-override count, and the keyword lists
(code/reasoning/technical/simple) and multi-step **regexes**. Defaults match LiteLLM's
ClawRouter-based implementation (`config.py`). Because the lists are just data, swapping
`TechnicalTerms` for your own vocabulary turns this into a domain-aware router for free.

**`ComplexityChatRouteSelector.cs`** is the scorer. The constructor validates the tier map and
precompiles the multi-step patterns into `Regex[]` (case-insensitive, culture-invariant, with a
100 ms match timeout as a ReDoS guard). `SelectRouteAsync`:

- Extracts the **last** user message and **last** system message (`ExtractUserAndSystem`).
- With **no user message**, prefers the explicit `defaultRoute`, else the `Medium`-tier route (there's
  nothing to score).
- Otherwise calls `Classify`, maps the tier to a route name (falling back to `defaultRoute` for an
  unmapped tier, and to the first registered route when the mapped name is not registered), and
  returns a **single-route plan** for that route (`SelectTarget`). A tier classifier picks exactly one
  route and has no meaningful ranking of the others, so it leaves fallback to the router's
  `onFailure` delegate.

`Classify` computes the ClawRouter-style weighted sum across the seven dimensions, applies the
"2+ distinct reasoning markers ⇒ `Reasoning`" override, then maps the score against the three
boundaries. The matching helpers keep the same semantics: single-word keywords match on **word
boundaries** (`ContainsWord`/`IsWordChar`, so `api` ≠ `capital`); multi-word phrases match as
substrings; bucket scores are discrete, not linear; code/technical/simple read system+user text while
reasoning markers, token count, and question count read the user message only. (The full scoring
table, formula, and a worked example are in
[`routing-chat-client.md`](./routing-chat-client.md#how-the-complexity-router-works).)

`ClassifyTier(messages)` is public so callers can get the tier without routing (no-user ⇒ `Medium`).

## The tests

The relevant test files live under
`test/Libraries/Microsoft.Extensions.AI.Tests/ChatRouting/` and run on `net10.0`.

### `RoutingChatClientTests.cs` — the mechanism (and selector integration)

The broadest suite. It exercises the orchestrator end-to-end with fake `IChatClient`s and confirms
each mechanism guarantee:

- **Construction/validation:** rejects empty route lists and routes without a client; the constructor
  rejects duplicate route names.
- **Default selector:** forwards the model id, stamps the response, and crucially **does not leak**
  routing internals into the forwarded request; honors an explicit `ChatOptions.ModelId`.
- **Fallback:** walks the plan on failure and stamps the model that actually succeeded; propagates the
  last exception when the chain is exhausted; throws when a selector routes to an unregistered route.
- **Streaming:** stamps only the first update; falls back before the first update is produced.
- **Plumbing:** `GetService` returns both the client and the selector; `Dispose` disposes each client
  exactly once (verified with a `CountingDisposeClient`).
- **Capability gate (integration):** an image request narrows to a route declaring `Vision`, and a
  request with an `AIFunctionDeclaration` tool narrows to a route declaring `FunctionCalling` (even
  under the opinion-free default selector); the gate falls through to the full set when no route
  declares the capability; a detector that returns no tokens bypasses it and routes to the
  otherwise-ineligible primary.
- **Shipped selectors (integration):** `SemanticChatRouteSelector`
  routes to the most similar model, aggregates a model's matches by mean (or sum/max), applies the
  global top-k, honors per-model thresholds, and falls back to the default model when nothing passes
  the threshold or the request has no user text.

### `ComplexityChatRouteSelectorTests.cs` — the complexity selector

Validates the ClawRouter-style scoring and the routing wrapper: constructor rejects null/empty tier maps;
`ClassifyTier` classifies a greeting as `Simple`, forces `Reasoning` on two reasoning markers, and
rates a code+technical prompt as `Complex`; `SelectRouteAsync` routes to the tier-mapped model, falls
back to the default model for an unmapped tier, and falls back to the first registered model when the
target model isn't present; and custom keyword lists change the classification (proving the options are honored).

---

## Mental model, in one line

**The mechanism is dumb and stable; the policy is smart and replaceable.** `RoutingChatClient` gives
you composition (nesting + middleware), fallback, and response stamping for free;
`IChatRouteSelector` supplies the judgment — whether that's the empty default, one of the shipped
experimental selectors, or your own lambda. `ChatRoutePlan` exists because a routing decision is
*ordered preference*, not just a single model; and when a selector picks one
model, the router's own `onFailure` delegate owns the order in which the remaining models are tried.
