# RoutingChatClient: a cookbook of use cases

> Everything here is **experimental** (diagnostic id `MEAI001`) and lives in the
> `Microsoft.Extensions.AI` namespace. This is a **samples-first** companion to the two existing
> design docs — read them for the *why*:
>
> - [`routing-chat-client.md`](./routing-chat-client.md) — the user-facing how-to (mechanism vs.
>   policy, the capability gate, telemetry).
> - [`chat-routing-architecture.md`](./chat-routing-architecture.md) — the file-by-file architecture
>   deep dive.
> - [`semantic-chat-client-selection.md`](./semantic-chat-client-selection.md) — the semantic
>   selector in depth.
>
> This document walks the archetypal scenarios end to end and shows the code for each.

## Where the types live

The routing feature is split across three packages, but every type stays in the
`Microsoft.Extensions.AI` namespace, so a single `using Microsoft.Extensions.AI;` covers all of it.

| Package | Kind | Types |
|---|---|---|
| `Microsoft.Extensions.AI.Abstractions` | Policy contract + metadata | `ChatRoute`, `ChatRouteCatalog`, `ChatModelCapabilities`, `IChatRouteSelector`, `ChatRouteSelector`, `ChatRouteContext`, `ChatRoutePlan` |
| `Microsoft.Extensions.AI` | Mechanism | `RoutingChatClient`, `DelegatingRoutingChatClient`, `UseRouting()`, `RouteFailureContext` |
| `Microsoft.Extensions.AI.Routing` | Shipped policies + helpers | `ComplexityChatRouteSelector`, `SemanticChatRouteSelector`, `StickyChatRouteSelector`, `CooldownGateChatRouteSelector`, `RouteCooldownStore` (+ their options/enums) |

The dependency arrow points one way: the `Routing` selectors reference the core abstractions, never
the reverse. You can adopt the mechanism and write your own selector without ever taking a dependency
on `Microsoft.Extensions.AI.Routing`.

## The 30-second model

- **Mechanism (`RoutingChatClient` / `UseRouting`)** — opinion-free. It owns the candidate routes,
  applies a soft capability gate, runs the selector **once per request**, walks fallbacks on failure,
  and stamps the chosen route onto the response.
- **Policy (`IChatRouteSelector`)** — all judgment about *which route is better* or *what the user is
  asking for*. Swappable. When you supply none, the default is deterministic: honor a caller-pinned
  `ChatOptions.ModelId`, else use the first registered route.

```
request ──▶ capability gate ──▶ selector (policy) ──▶ ChatRoutePlan ──▶ fallback walk ──▶ response (+ chosen route stamped)
                                                                             ▲
                                                                       onFailure delegate
```

---

## 1. `RoutingChatClient` and `UseRouting()`

There are **two front doors** onto the exact same engine. They differ only in what they dispatch to.

### `RoutingChatClient` — the multiplexer (many clients)

`RoutingChatClient` is a plain `IChatClient` that owns **N distinct inner clients**, one bound to
each route. Use it when the routes are genuinely *different clients* — different providers, different
endpoints, different credentials, or a nested router.

```csharp
public RoutingChatClient(
    IReadOnlyList<ChatRoute> routes,                                                  // ≥ 1, each bound to an IChatClient
    IChatRouteSelector? selector = null,                                              // policy; null = opinion-free default
    Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure = null,           // fallback policy (§3)
    Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>>? capabilityDetector = null); // gate override
```

### `UseRouting()` / `DelegatingRoutingChatClient` — the profile router (one client)

`DelegatingRoutingChatClient` wraps **one** inner client and treats its routes as pure metadata. A
selector picks a route, and the router *shapes the request* — supplying that route's `ModelId` and
`ReasoningEffort` — before forwarding to the same inner client. Use it when a single provider client
can already serve many models/efforts by honoring a per-request `ChatOptions.ModelId`. It composes in
a `ChatClientBuilder` pipeline:

```csharp
public static ChatClientBuilder UseRouting(
    this ChatClientBuilder builder,
    IReadOnlyList<ChatRoute> routes,
    IChatRouteSelector? selector = null,
    Func<RouteFailureContext, IReadOnlyList<ChatRoute>?>? onFailure = null,
    Func<IEnumerable<ChatMessage>, ChatOptions?, IReadOnlyCollection<string>>? capabilityDetector = null);
```

Because it *delegates*, it preserves the inner client's identity: `GetService` and `ChatClientMetadata`
pass through, so downstream telemetry sees the real provider rather than a synthetic `"routing"` layer.

### Which one do I use?

| Your routes are… | Front door | Route needs a bound `Client`? |
|---|---|---|
| Different **providers/clients** (OpenAI *and* Anthropic *and* Gemini) | `RoutingChatClient` | Yes — one client per route |
| Different **models on one provider** where one client serves all via `ModelId` (OpenAI, Anthropic, Gemini, …) | `UseRouting()` | No — `modelId` only |
| The **same model** at different **reasoning efforts** | `UseRouting()` | No — `modelId` + `reasoningEffort` |
| A mix, or nested routers | `RoutingChatClient` (nest freely) | Yes |

> **Rule of thumb:** if each route is a different `IChatClient`, reach for `RoutingChatClient`. If
> every route is the *same* client shaped differently, reach for `UseRouting()`.

### 1a. Cross-provider selection (Anthropic + Gemini + OpenAI)

Each provider is its own `IChatClient`, so each route is **bound** to its client. This is the
canonical `RoutingChatClient` shape.

```csharp
using Microsoft.Extensions.AI;

// Provider clients your app already builds (each is an IChatClient).
IChatClient anthropic = CreateAnthropicClient();   // e.g. Claude
IChatClient gemini    = CreateGeminiClient();       // e.g. Gemini
IChatClient openai    = CreateOpenAIClient();        // e.g. GPT

IChatClient router = new RoutingChatClient(
[
    new ChatRoute(
        name: "claude-sonnet",
        providerName: "anthropic",
        modelId: "claude-sonnet-4",
        additionalProperties: new AdditionalPropertiesDictionary
        {
            [ChatModelCapabilities.PropertyKey] =
                new[] { ChatModelCapabilities.Vision, ChatModelCapabilities.FunctionCalling },
        },
        client: anthropic),

    new ChatRoute(
        name: "gemini-flash",
        providerName: "google",
        modelId: "gemini-2.5-flash",
        additionalProperties: new AdditionalPropertiesDictionary
        {
            [ChatModelCapabilities.PropertyKey] =
                new[] { ChatModelCapabilities.Vision, ChatModelCapabilities.FunctionCalling },
        },
        client: gemini),

    new ChatRoute(
        name: "gpt-mini",
        providerName: "openai",
        modelId: "gpt-4o-mini",
        additionalProperties: new AdditionalPropertiesDictionary
        {
            [ChatModelCapabilities.PropertyKey] =
                new[] { ChatModelCapabilities.Vision, ChatModelCapabilities.FunctionCalling },
        },
        client: openai),
]);

// With no selector, the router honors ChatOptions.ModelId, else the first route (claude-sonnet).
var response = await router.GetResponseAsync(
    "Summarize this PDF.",
    new ChatOptions { ModelId = "gemini-2.5-flash" }); // pins the gemini route
```

Because each candidate is itself an `IChatClient`, you can give any one of them its own middleware
(e.g. wrap `openai` in `.AsBuilder().UseFunctionInvocation().Build()` before binding it), and you can
wrap the whole router the same way.

### 1b. Mono-provider selection (many models on one provider client)

`UseRouting()` works with **any** provider whose `IChatClient` honors a per-request
`ChatOptions.ModelId` — which most MEAI integrations do, not just OpenAI. So a single provider client
can serve many models and you never build N clients. Known examples:

- **OpenAI / Azure OpenAI** — `Microsoft.Extensions.AI.OpenAI`.
- **Anthropic** — [`Anthropic.SDK`](https://www.nuget.org/packages/Anthropic.SDK)'s MEAI integration
  ([docs](https://deepwiki.com/tghamm/Anthropic.SDK/4.2-microsoft.extensions.ai-integration)); its
  `IChatClient` reads the per-request model id.
- **Gemini** —
  [`GeminiDotnet.Extensions.AI`](https://www.nuget.org/packages/GeminiDotnet.Extensions.AI); one
  client, model selected per request.

Routes are **metadata only** (`modelId`, no bound `Client`), and the router forwards the chosen
route's `ModelId` to the one inner client.

```csharp
using Microsoft.Extensions.AI;

// Any single provider client that honors ChatOptions.ModelId — OpenAI, Anthropic, Gemini, ...
IChatClient provider = CreateProviderClient();

// Anthropic model ids shown; swap for gpt-4o-mini/gpt-4o (OpenAI) or gemini-2.5-flash/-pro (Gemini).
ChatRoute[] routes =
[
    new ChatRoute("small",  modelId: "claude-haiku-4"),
    new ChatRoute("medium", modelId: "claude-sonnet-4"),
    new ChatRoute("large",  modelId: "claude-opus-4"),
];

// A trivial cost-first selector (see §2) that prefers the cheapest by default.
IChatClient client = provider
    .AsBuilder()
    .UseRouting(routes, selector: new CheapestRouteSelector())
    .Build();

// The router sets options.ModelId = "claude-haiku-4" (unless the caller pinned one) and forwards
// to the same provider client. Downstream telemetry still sees the real provider, not "routing".
var response = await client.GetResponseAsync("What's the capital of France?");
```

> **Requirement:** the inner client must actually honor per-request `ModelId`. If a client is hard-bound
> to a single model and ignores `ModelId`, `UseRouting()` can't switch models over it — use separate
> clients bound per route with `RoutingChatClient` (§1a) instead.

### 1c. Mono-model selection (GPT-5.5 at low / medium / high reasoning)

The most surgical case: one model offered as several routes that differ **only** by
`ReasoningEffort`. `UseRouting()` shines here because the inner client is literally the same model —
the router only patches `ChatOptions.Reasoning.Effort`.

```csharp
using Microsoft.Extensions.AI;

IChatClient gpt55 = CreateOpenAIClient(); // bound to gpt-5.5

ChatRoute[] efforts =
[
    new ChatRoute("fast",     modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.Low),
    new ChatRoute("balanced", modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.Medium),
    new ChatRoute("deep",     modelId: "gpt-5.5", reasoningEffort: ReasoningEffort.High),
];

// Route harder prompts to higher effort. The complexity selector (§5) is a natural fit:
var selector = new ComplexityChatRouteSelector(
    new Dictionary<ChatComplexityTier, string>
    {
        [ChatComplexityTier.Simple]    = "fast",
        [ChatComplexityTier.Medium]    = "balanced",
        [ChatComplexityTier.Complex]   = "deep",
        [ChatComplexityTier.Reasoning] = "deep",
    },
    defaultRoute: "balanced");

IChatClient client = gpt55.AsBuilder().UseRouting(efforts, selector).Build();

// "Hi there" → Low effort; a multi-step analysis prompt → High effort. Same model, same client.
var quick = await client.GetResponseAsync("Say hi.");
var hard  = await client.GetResponseAsync("Think step by step and prove the four-color theorem sketch.");
```

> Forwarding is **advisory and non-destructive**: the router only sets `ModelId`/`Reasoning.Effort`
> when the caller did **not** already pin them. An explicit `ChatOptions.Reasoning.Effort` from the
> caller always wins. Providers that don't support reasoning effort simply ignore it.

> **`ReasoningEffort` isn't exclusive to `UseRouting()`.** Both front doors share the same engine and
> the same `ChatRoute.ReasoningEffort` forwarding, so `RoutingChatClient` (§1a) applies a route's
> reasoning effort too — each bound client gets the chosen route's `ModelId` **and** `ReasoningEffort`.
> You could, for example, give one cross-provider route a fixed high effort and another a low effort.
> The mono-model shape above is simply the most *archetypal* place to vary reasoning levels, because
> there the effort is the **only** thing that changes between routes.

### Registering in DI

Both front doors compose with the standard `AddChatClient` / `ChatClientBuilder` infrastructure — no
routing-specific registration helper:

```csharp
// Multiplexer:
services.AddChatClient(sp => new RoutingChatClient(
[
    new ChatRoute("openai", client: sp.GetRequiredKeyedService<IChatClient>("openai")),
    new ChatRoute("anthropic", client: sp.GetRequiredKeyedService<IChatClient>("anthropic")),
]))
.UseFunctionInvocation()
.UseOpenTelemetry();

// Profile router over one client:
services.AddChatClient(sp => sp.GetRequiredKeyedService<IChatClient>("openai"))
    .UseRouting(efforts, selector);
```

---

## 2. Writing a selector

A selector is the policy. It reads a `ChatRouteContext` and returns a `ChatRoutePlan`.

```csharp
public interface IChatRouteSelector
{
    ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default);
}
```

**In — `ChatRouteContext`:**

| Member | What it gives you |
|---|---|
| `Messages` | the chat messages being routed |
| `Options` | the request's `ChatOptions` (or `null`) — read caller hints like `ModelId`, `ConversationId` |
| `Routes` | the **candidate routes** (already narrowed by the capability gate) |

**Out — `ChatRoutePlan`:**

- `OrderedRoutes` — the routes you prefer, **primary first**; the rest are fallbacks the router tries
  in order (§3).
- `DecisionMetadata` — optional rationale (e.g. a tier or score) surfaced on the `routing.decision`
  trace event. Behavior-free.

### The one hard rule: reference identity

Every route in the plan **must be one of the exact `context.Routes` instances**. The router matches
by reference, not by value — a reconstructed `ChatRoute` with identical metadata makes the router
throw. Resolve by name against `context.Routes`:

```csharp
ChatRoute pick = context.Routes.First(r =>
    string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase));
```

### Inline selectors with `ChatRouteSelector.Create`

For a quick policy you don't need a class:

```csharp
IChatRouteSelector firstOnly = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

IChatRouteSelector byHint = ChatRouteSelector.Create((ctx, ct) =>
{
    // e.g. read an app hint off ChatOptions.AdditionalProperties and pick a route.
    string wanted = ctx.Options?.AdditionalProperties?.TryGetValue("tier", out object? v) == true
        ? v?.ToString() ?? "std" : "std";
    ChatRoute pick = ctx.Routes.FirstOrDefault(r => r.Name == wanted) ?? ctx.Routes[0];
    return new ValueTask<ChatRoutePlan>(new ChatRoutePlan(pick));
});
```

### A real custom selector: cheapest-that-fits

This reads the advisory `ChatRoute` metadata (cost, context window) that no built-in selector reads,
and returns a **ranked** plan so the tail is a meaningful fallback chain.

```csharp
public sealed class CheapestRouteSelector : IChatRouteSelector
{
    public ValueTask<ChatRoutePlan> SelectRouteAsync(ChatRouteContext context, CancellationToken cancellationToken = default)
    {
        // Rough token estimate of the request (chars/4).
        int approxTokens = context.Messages.Sum(m => m.Text.Length) / 4;

        ChatRoute[] ranked = context.Routes
            .Where(r => r.MaxInputTokens is null || r.MaxInputTokens >= approxTokens)  // context-window filter
            .OrderBy(r => r.InputTokenCostPerMillion ?? decimal.MaxValue)              // cheapest first
            .ToArray();

        // If nothing fits, fall back to the full candidate set rather than stranding the request.
        if (ranked.Length == 0)
        {
            ranked = context.Routes.ToArray();
        }

        return new ValueTask<ChatRoutePlan>(new ChatRoutePlan(ranked));
    }
}
```

Selectors compose as decorators — a selector can wrap another selector and adjust its plan. The
built-in `StickyChatRouteSelector` and `CooldownGateChatRouteSelector` are exactly this (§3, §5).

---

## 3. `onFailure`: fallback and circuit-breaking

A selector that naturally picks **one** route (like a complexity classifier) has no honest ranking of
the *other* routes. That's what `onFailure` is for. It is the router's fallback policy, invoked on
**every pre-commit dispatch failure** with a `RouteFailureContext`:

| `RouteFailureContext` | Meaning |
|---|---|
| `Route` | the route that just threw |
| `Exception` | the thrown exception, **unclassified** — you interpret it |
| `AttemptNumber` | 1-based count of routes tried so far |
| `Remaining` | the still-untried candidates (plan tail + registered routes the plan omitted) |
| `Options` / `Messages` | the request |

Return the routes to try next, in order — any subset of `Remaining`, reordered as you like — or
`null`/empty to **stop and rethrow**. Returned routes are de-duped against those already tried, so
routing always terminates.

> **Streaming caveat:** `onFailure` applies only *before the first update is yielded*. Once a token is
> on the wire, no re-routing occurs. Cancellation is never treated as a failure.

### 3a. The simplest fallback: try everything left

```csharp
var client = new RoutingChatClient(
[
    new ChatRoute("fast",  client: fastClient),
    new ChatRoute("smart", client: smartClient),
],
    selector: new ComplexityChatRouteSelector(tierMap), // picks exactly one route
    onFailure: ctx => ctx.Remaining);                    // on any failure, try the rest in order
```

### 3b. Blast-radius pruning on an auth error

Interpret the exception and prune whole providers. On a 401 there's no point trying other routes that
share the dead credential:

```csharp
onFailure: ctx =>
{
    if (ctx.Exception is ClientResultException { Status: 401 or 403 })
    {
        // Drop every remaining route from the same provider as the one that failed.
        return ctx.Remaining
            .Where(r => !string.Equals(r.ProviderName, ctx.Route.ProviderName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    return ctx.Remaining; // otherwise just fall through to the next candidate
}
```

### 3c. Cooldown on rate-limit / overload (429 / 503 + `Retry-After`)

This is the archetypal resilience pattern. Pair a **write half** (`onFailure` cools a route on a
transient HTTP status, honoring `Retry-After`) with a **read half** (a `CooldownGateChatRouteSelector`
hides cooling routes from the selector). `RouteCooldownStore` is the shared, thread-safe state.

```csharp
using System.ClientModel;             // ClientResultException (OpenAI/Azure adapters surface HTTP here)
using Microsoft.Extensions.AI;

var cooldowns = new RouteCooldownStore();

// READ half: the selector never even considers a cooling route.
IChatRouteSelector selector = new CooldownGateChatRouteSelector(
    cooldowns,
    inner: new CheapestRouteSelector());   // any inner policy

// WRITE half: on a transient failure, cool the route and move on.
Func<RouteFailureContext, IReadOnlyList<ChatRoute>?> onFailure = ctx =>
{
    if (ctx.Exception is ClientResultException { Status: 429 or 503 } ex)
    {
        cooldowns.Cool(ctx.Route.Name, GetRetryAfter(ex) ?? TimeSpan.FromSeconds(30));

        // Also drop still-cooling routes from *this* request's fallback walk, so a route the gate
        // let through earlier isn't retried now that it's cooling.
        return ctx.Remaining.Where(r => !cooldowns.IsCooled(r.Name)).ToList();
    }

    return ctx.Remaining; // non-transient: fall through without cooling
};

var client = new RoutingChatClient(routes, selector, onFailure);

static TimeSpan? GetRetryAfter(ClientResultException ex)
{
    // Retry-After may be seconds or an HTTP-date.
    if (ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out string? value) == true)
    {
        if (int.TryParse(value, out int seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(value, out DateTimeOffset when))
        {
            return when - DateTimeOffset.UtcNow;
        }
    }

    return null;
}
```

Why both halves? The gate filters what the **selector** sees, but `onFailure` looks ahead over *all*
remaining registered routes and could otherwise re-introduce a cooling route as a fallback. Cooling in
`onFailure` **and** filtering it back out (the `Where(...IsCooled)` above) closes the loop. Clear a
cooldown early from a success hook with `cooldowns.Clear(routeName)` if you track recoveries.

For **testability**, inject a clock: `new RouteCooldownStore(now: () => fakeClock.UtcNow)`.

---

## 4. Ingesting a model catalog (e.g. LiteLLM)

Selectors need per-model facts (capabilities, context window, cost) and those facts change as
providers ship models. `ChatRoute` carries advisory metadata for exactly this, and `ChatRouteCatalog`
stores **client-less** entries you bind to a client later with `WithClient`. Mapping external catalog
JSON into `ChatRoute` entries is left to you, because formats and provenance differ.

Here's a complete loader for LiteLLM's
[`model_prices_and_context_window.json`](https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json)
— a maintained, per-model catalog of `supports_*` capability flags, context windows, and token
pricing. It maps the `supports_*` flags onto `ChatModelCapabilities` tokens and converts per-token
cost to per-million.

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

static ChatRouteCatalog LoadLiteLlmCatalog(string json, Uri? source = null)
{
    using JsonDocument doc = JsonDocument.Parse(json);
    var entries = new List<ChatRoute>();

    foreach (JsonProperty model in doc.RootElement.EnumerateObject())
    {
        // The file ships a documentation-only "sample_spec" pseudo-entry — skip it.
        if (model.Name == "sample_spec" || model.Value.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        JsonElement spec = model.Value;

        // Map LiteLLM supports_* flags onto capability tokens.
        var caps = new List<string>();
        void AddIf(string field, string token)
        {
            if (spec.TryGetProperty(field, out JsonElement e) && e.ValueKind == JsonValueKind.True)
            {
                caps.Add(token);
            }
        }

        AddIf("supports_vision", ChatModelCapabilities.Vision);
        AddIf("supports_function_calling", ChatModelCapabilities.FunctionCalling);
        AddIf("supports_response_schema", ChatModelCapabilities.ResponseSchema);
        AddIf("supports_pdf_input", ChatModelCapabilities.PdfInput);
        AddIf("supports_audio_input", ChatModelCapabilities.AudioInput);
        AddIf("supports_reasoning", ChatModelCapabilities.Reasoning);
        AddIf("supports_web_search", ChatModelCapabilities.WebSearch);

        AdditionalPropertiesDictionary? props = caps.Count > 0
            ? new AdditionalPropertiesDictionary { [ChatModelCapabilities.PropertyKey] = caps.ToArray() }
            : null;

        entries.Add(new ChatRoute(
            name: model.Name,                                               // e.g. "gpt-4o-mini"
            providerName: GetString(spec, "litellm_provider"),
            modelId: model.Name,
            maxInputTokens: GetInt(spec, "max_input_tokens"),
            inputTokenCostPerMillion: PerMillion(spec, "input_cost_per_token"),
            outputTokenCostPerMillion: PerMillion(spec, "output_cost_per_token"),
            sourceUri: source,
            updatedAt: DateTimeOffset.UtcNow,
            additionalProperties: props));
    }

    return new ChatRouteCatalog(entries);

    static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : null;

    static decimal? PerMillion(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetDecimal(out decimal d) ? d * 1_000_000m : null;
}
```

Use it: keep the catalog as reusable metadata, then bind the entries you route between to concrete
clients. The `CheapestRouteSelector` from §2 now has real cost/context data to work with.

### Catalog + `RoutingChatClient` (bind per route)

```csharp
string json = await httpClient.GetStringAsync(
    "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");

ChatRouteCatalog catalog = LoadLiteLlmCatalog(json,
    source: new Uri("https://github.com/BerriAI/litellm"));

var router = new RoutingChatClient(
[
    catalog.Get("gpt-4o-mini").WithClient(openai),  // metadata from catalog, client bound here
    catalog.Get("gpt-4o").WithClient(openai),
    catalog.CreateRoute("claude-3-5-sonnet-20241022", anthropic), // shorthand for Get(name).WithClient(client)
],
    selector: new CheapestRouteSelector());
```

> The catalog's capability tokens automatically feed the router's soft capability gate: a request
> carrying an image is narrowed to routes that declared `vision`, and a request with tools to routes
> that declared `function_calling` — no selector code required.

### Catalog + `UseRouting()` (one client, no binding)

With the single-client front door the catalog is even simpler: `UseRouting()` routes are
**metadata-only**, so you don't bind a client at all — the entries already carry `modelId` (and cost,
context, capabilities), and the one inner provider client serves them all. Pull the entries you want
straight off the catalog with `catalog.Get(name)` — no `WithClient`:

```csharp
using Microsoft.Extensions.AI;

IChatClient openai = CreateOpenAIClient(); // one client, honors ChatOptions.ModelId

IChatClient router = openai.AsBuilder()
    .UseRouting(
        [
            catalog.Get("gpt-4o-mini"),   // no WithClient — metadata only
            catalog.Get("gpt-4o"),
            catalog.Get("o4-mini"),
        ],
        selector: new CheapestRouteSelector())   // sees the same cost/context metadata
    .Build();

// The chosen entry's ModelId (e.g. "gpt-4o-mini") is forwarded to the one OpenAI client, and the
// same catalog capability tokens still drive the soft capability gate.
var response = await router.GetResponseAsync("What's the capital of France?");
```

Reach for the bound-client `RoutingChatClient` form above when the catalog spans **multiple**
providers (each needs its own client); reach for this `UseRouting()` form when every routed model is
served by **one** client that honors `ModelId`.

---

## 5. Built-in selectors, and making routing sticky

### 5a. Semantic selector (embedding similarity)

Give each route a few representative example phrases (its *profile*); the selector embeds the user's
message and routes to the closest-matching profile. Needs only an
`IEmbeddingGenerator<string, Embedding<float>>`.

```csharp
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> embeddings = CreateEmbeddingGenerator();

var selector = new SemanticChatRouteSelector(
    embeddings,
    routeProfiles: new Dictionary<string, IReadOnlyList<string>>
    {
        ["code"] = ["write a function", "fix this bug", "refactor this class", "why won't this compile"],
        ["chat"] = ["how are you", "tell me a joke", "what's the weather like", "let's chat"],
        ["math"] = ["solve for x", "integrate this", "prove this theorem", "what's the derivative"],
    },
    defaultRoute: "chat");   // used when nothing clears the score threshold

var router = new RoutingChatClient(
[
    new ChatRoute("code", client: codeClient),
    new ChatRoute("chat", client: chatClient),
    new ChatRoute("math", client: mathClient),
],
    selector);
```

Profiles are embedded once and cached. Because the semantic selector produces a **ranked** plan
(primary first, others by descending similarity), its tail *is* a fallback chain — you often don't
need `onFailure` with it. Tune `TopK`, `Aggregation`, and `ScoreThreshold` (global or per-route) via
`SemanticRouterOptions`. See [`semantic-chat-client-selection.md`](./semantic-chat-client-selection.md).

The selector is **front-door agnostic** — it only picks a route by name — so the *same* instance drives
`UseRouting()` when your routes are models on one client. Keep the profile keys equal to the route
names and give each route a `modelId`:

```csharp
IChatClient openai = CreateOpenAIClient();

ChatRoute[] routes =
[
    new ChatRoute("code", modelId: "gpt-4o"),      // profile key "code" → this route
    new ChatRoute("chat", modelId: "gpt-4o-mini"), // profile key "chat"
    new ChatRoute("math", modelId: "o3"),          // profile key "math"
];

IChatClient client = openai.AsBuilder().UseRouting(routes, selector).Build();
```

### 5b. Complexity selector (rule-based, zero-latency)

Deterministic keyword/pattern scoring classifies each request into a `ChatComplexityTier`
(`Simple`/`Medium`/`Complex`/`Reasoning`), then routes to the route you mapped to that tier. No I/O,
sub-millisecond. The tier→route map is **explicit** and required — the selector makes no judgment
about which route is "better".

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

var router = new RoutingChatClient(routes, selector, onFailure: ctx => ctx.Remaining);
```

A tier classifier picks exactly one route, so its plan is a single route — pair it with `onFailure`
(§3) for resilience. Every weight, threshold, and keyword list is on `ComplexityRouterOptions`;
swapping `TechnicalTerms` for your domain vocabulary makes it domain-aware for free. `ClassifyTier`
is public if you want the tier without routing.

The tier map's values are route **names**, so this selector drops straight into `UseRouting()` over one
client — name each route for its model and set a matching `modelId` (the `onFailure` tail still gives
the single-route plan resilience):

```csharp
IChatClient openai = CreateOpenAIClient();

ChatRoute[] routes =
[
    new ChatRoute("gpt-4o-mini", modelId: "gpt-4o-mini"),
    new ChatRoute("gpt-4o",      modelId: "gpt-4o"),
    new ChatRoute("o3",          modelId: "o3"),
];

IChatClient client = openai
    .AsBuilder()
    .UseRouting(routes, selector, onFailure: ctx => ctx.Remaining)
    .Build();
```

### 5c. Making it sticky (pin a conversation to a route)

`StickyChatRouteSelector` layers conversation stickiness onto *any* inner selector. Crucially, it
holds **no state**: both the state (*which route a conversation is pinned to*) and the trigger
(*when to pin/release*) are your application's. Each turn it calls your `getPins` callback and, if the
returned route names still resolve to current candidates, pins to them; otherwise it defers to the
inner selector.

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

// App-owned pin state, keyed by conversation. You decide when to write it.
var pinsByConversation = new ConcurrentDictionary<string, IReadOnlyList<string>>();

var sticky = new StickyChatRouteSelector(
    getPins: ctx =>
    {
        string? convo = ctx.Options?.ConversationId;
        return convo is not null && pinsByConversation.TryGetValue(convo, out var pins) ? pins : null;
    },
    inner: new SemanticChatRouteSelector(embeddings, profiles, defaultRoute: "chat"));

var router = new RoutingChatClient(routes, sticky);

// Elsewhere in your app — after the first turn picks a route, pin the conversation to it so
// follow-ups stay on the same model:
pinsByConversation["conversation-42"] = ["code"];
```

A stale pin (e.g. to a route the capability gate excluded this turn, or one a `CooldownGate` is hiding
while it cools) simply doesn't resolve and is skipped — it can never dead-end a turn, and re-attaches
for free once the route is a candidate again. When a pin resolves, the decision is tagged
`routing.pinned` for telemetry.

### 5d. Layering selectors

Selectors are decorators, so you can stack them. A robust production stack: **sticky** (prefer the
pinned route) → **cooldown gate** (hide unhealthy routes) → **complexity** (choose by difficulty):

```csharp
var cooldowns = new RouteCooldownStore();

IChatRouteSelector selector =
    new StickyChatRouteSelector(getPins,
        new CooldownGateChatRouteSelector(cooldowns,
            new ComplexityChatRouteSelector(tierMap, defaultRoute: "gpt-4o")));

var router = new RoutingChatClient(
    routes,
    selector,
    onFailure: ctx =>
    {
        if (ctx.Exception is ClientResultException { Status: 429 or 503 } ex)
        {
            cooldowns.Cool(ctx.Route.Name, GetRetryAfter(ex) ?? TimeSpan.FromSeconds(30));
        }

        return ctx.Remaining.Where(r => !cooldowns.IsCooled(r.Name)).ToList();
    });
```

---

## 6. More archetypes

### Nested routers (a routing tree)

Because each route's `Client` is itself an `IChatClient`, a route can be **another
`RoutingChatClient`**. Route coarsely at the top (e.g. by region or tenant), finely below (e.g. by
complexity):

```csharp
IChatClient usRouter = new RoutingChatClient(usRoutes, new ComplexityChatRouteSelector(usTierMap));
IChatClient euRouter = new RoutingChatClient(euRoutes, new ComplexityChatRouteSelector(euTierMap));

IChatClient root = new RoutingChatClient(
[
    new ChatRoute("us", client: usRouter),
    new ChatRoute("eu", client: euRouter),
],
    selector: regionSelector);
```

Telemetry composes cleanly: each router opens its own span (source
`RoutingChatClient.ActivitySourceName`), and the winning path is stamped on the response under
`RoutingChatClient.SelectedPathKey` (e.g. `"eu/gpt-4o-mini"`).

### Bypassing the capability gate

The gate is soft and default-on. To disable it entirely (let the selector / `ModelId` have the final
say — useful when you don't trust the catalog's capability metadata), pass a detector that requires
nothing:

```csharp
var router = new RoutingChatClient(
    routes,
    selector,
    capabilityDetector: static (_, _) => Array.Empty<string>()); // no route is ever gated out
```

### Reading the routing decision off the response

The chosen route is stamped on the **response** (never mutated into the request):

```csharp
var response = await router.GetResponseAsync(messages);

response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedRouteNameKey, out object? route);
response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedModelIdKey, out object? modelId);
response.AdditionalProperties!.TryGetValue(RoutingChatClient.SelectedPathKey, out object? path);
// route     → the router-local alias that answered
// modelId   → the concrete billed model (use this for cost/usage attribution)
// path      → the full winning path through any nested routers
```

For the full trace-event schema (`routing.decision`, `routing.attempt`) see
[`routing-chat-client.md`](./routing-chat-client.md#routing-telemetry-trace-events).

---

## Cheat sheet

| I want to… | Reach for |
|---|---|
| Route across **different providers/clients** | `RoutingChatClient` with each route bound via `client:` / `WithClient` |
| Route across **models on one client** | `UseRouting()` with `modelId`-only routes |
| Route across **reasoning efforts** of one model | `UseRouting()` with `modelId` + `reasoningEffort` routes |
| Pick by **difficulty**, no latency | `ComplexityChatRouteSelector` |
| Pick by **meaning** of the message | `SemanticChatRouteSelector` |
| **Cheapest that fits** | custom `IChatRouteSelector` reading `ChatRoute` cost/context metadata |
| **Fallback** when a route fails | `onFailure` delegate |
| **Skip** rate-limited routes | `RouteCooldownStore` + `CooldownGateChatRouteSelector` + `onFailure` |
| **Pin** a conversation to a route | `StickyChatRouteSelector` (app owns the pin state) |
| Reuse **model metadata** | `ChatRouteCatalog` (+ your JSON loader) bound with `WithClient` |
