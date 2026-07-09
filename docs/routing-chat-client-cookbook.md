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
| `Microsoft.Extensions.AI.Abstractions` | `ChatRoute`, `ChatRouteCatalog` |
| `Microsoft.Extensions.AI` | `RoutingChatClient` (abstract) |

## The 30-second model

There is **one policy seam**: `RoutingChatClient.SelectNextRouteAsync`. The base class calls it in a
loop and asks the same question each time — *given the request and what has been attempted (and why the
last attempt failed), which route next, or `null` to stop?*

- **Selection** = the first call (`attempted` empty, `lastException` null).
- **Failover** = a later call (`lastException` set, `attempted[^1]` is the route that failed).
- **Filtering** = simply not returning a route.

Rules the base class enforces: the returned route must be one of the registered instances (by
reference); a route already in `attempted` stops the loop; `null` on the first call throws, `null` after
a failure rethrows the last exception; cancellation never triggers fallback; when streaming, fallback
stops once the first token is on the wire.

You **derive from `RoutingChatClient`** and override the one method. The simplest policy — ordered
failover (try each route until one succeeds) — is the short subclass shown in section 1; richer policies
follow the same shape.

---

## 1. Ordered failover

The most common policy. It honors an explicitly requested model first, otherwise the first route, then
falls back through the remaining routes in registration order. It's a short `RoutingChatClient` subclass
— copy it as-is or use it as a template:

```csharp
using Microsoft.Extensions.AI;

public sealed class OrderedFailoverClient : RoutingChatClient
{
    public OrderedFailoverClient(IReadOnlyList<ChatRoute> routes) : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        if (attempted.Count == 0)
        {
            ChatRoute? pinned = options?.ModelId is { } id
                ? routes.FirstOrDefault(r =>
                    string.Equals(r.ModelId, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase))
                : null;

            return new(pinned ?? routes[0]);
        }

        return new(routes.Except(attempted).FirstOrDefault());
    }
}
```

Bind each route to its `IChatClient` and list them in fallback order:

```csharp
IChatClient anthropic = CreateAnthropicClient();
IChatClient gemini    = CreateGeminiClient();
IChatClient openai    = CreateOpenAIClient();

IChatClient router = new OrderedFailoverClient(
[
    new ChatRoute("claude-sonnet", providerName: "anthropic", modelId: "claude-sonnet-4", client: anthropic),
    new ChatRoute("gemini-flash",  providerName: "google",    modelId: "gemini-2.5-flash", client: gemini),
    new ChatRoute("gpt-mini",      providerName: "openai",     modelId: "gpt-4o-mini",      client: openai),
]);

// No pin → first route (claude-sonnet); if it fails before output, fall back in order.
var a = await router.GetResponseAsync("Summarize this PDF.");

// Pin a route by model id (or by route name).
var b = await router.GetResponseAsync(
    "Summarize this PDF.",
    new ChatOptions { ModelId = "gemini-2.5-flash" });
```

Because each candidate is itself an `IChatClient`, you can give any one of them its own middleware
(for example, wrap `openai` in `.AsBuilder().UseFunctionInvocation().Build()` before binding it), and you
can wrap the whole router the same way.

### Register in DI

```csharp
services.AddChatClient(sp => new OrderedFailoverClient(
[
    new ChatRoute("primary", client: sp.GetRequiredKeyedService<IChatClient>("primary")),
    new ChatRoute("backup",  client: sp.GetRequiredKeyedService<IChatClient>("backup")),
]));
```

---

## 2. Writing a policy: derive from `RoutingChatClient`

Override `SelectNextRouteAsync`. Everything else — dispatch, fallback, telemetry, disposal — is the base
class's job.

### The one hard rule: reference identity

Every route you return **must be one of the exact `routes` instances**. The router matches by reference,
not by value — a reconstructed `ChatRoute` with identical metadata makes the router throw. Resolve by
name against `routes`:

```csharp
ChatRoute pick = routes.First(r =>
    string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase));
```

### Cheapest-that-fits (reads advisory metadata)

This reads the `ChatRoute` cost and context-window hints that ordered failover ignores, and returns
the cheapest route not yet tried — so the loop forms a cost-ordered fallback chain.

```csharp
public sealed class CheapestRouteClient : RoutingChatClient
{
    public CheapestRouteClient(IReadOnlyList<ChatRoute> routes) : base(routes) { }

    protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IReadOnlyList<ChatRoute> routes,
        IReadOnlyList<ChatRoute> attempted,
        Exception? lastException,
        CancellationToken cancellationToken)
    {
        int approxTokens = messages.Sum(m => m.Text.Length) / 4;

        ChatRoute? next = routes
            .Except(attempted)
            .Where(r => r.MaxInputTokens is null || r.MaxInputTokens >= approxTokens) // context-window filter
            .OrderBy(r => r.InputTokenCostPerMillion ?? decimal.MaxValue)             // cheapest first
            .FirstOrDefault();

        return new ValueTask<ChatRoute?>(next);
    }
}
```

### Classify once, then fall back

A policy that classifies the request (difficulty, intent) picks **one** route on the first call, then
falls back through the rest on later calls. Selection and failover are the same method — branch on
`lastException`:

```csharp
protected override ValueTask<ChatRoute?> SelectNextRouteAsync(
    IEnumerable<ChatMessage> messages, ChatOptions? options,
    IReadOnlyList<ChatRoute> routes, IReadOnlyList<ChatRoute> attempted,
    Exception? lastException, CancellationToken cancellationToken)
{
    if (lastException is null)
    {
        // First call: classify and pick one route.
        ChatRoute chosen = IsHard(messages) ? Named(routes, "smart") : Named(routes, "fast");
        return new ValueTask<ChatRoute?>(chosen);
    }

    // Later calls: fall back to the next untried route in order.
    return new ValueTask<ChatRoute?>(routes.Except(attempted).FirstOrDefault());
}
```

### Blast-radius pruning on an auth error

Interpret `lastException` and prune whole providers. On a 401 there's no point trying other routes that
share the dead credential — just don't return them:

```csharp
if (lastException is ClientResultException { Status: 401 or 403 })
{
    ChatRoute dead = attempted[^1];
    return new ValueTask<ChatRoute?>(routes
        .Except(attempted)
        .Where(r => !string.Equals(r.ProviderName, dead.ProviderName, StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault());
}
```

### Filtering: skip an unhealthy route

There is no separate candidate filter — filtering *is* not returning a route. Keep your own health/
cooldown state (seeded from the `protected Routes` property) and skip cooled routes:

```csharp
ChatRoute? next = routes
    .Except(attempted)
    .FirstOrDefault(r => !_cooldown.IsCooling(r.Name));

// next may be null → the router stops (first call throws; after a failure it rethrows).
```

To *write* a cooldown when a route fails, inspect `lastException` on the next call — for example, read a
`Retry-After` header off a `ClientResultException` and record a cool-until time for `attempted[^1]`
before choosing the next route.

---

## 3. Ingesting a model catalog (e.g. LiteLLM)

Map an external catalog into `ChatRoute` metadata, store it in a `ChatRouteCatalog`, then bind clients
per route. Catalog parsing is the caller's job; here is a compact LiteLLM-style loader.

```csharp
using System.Text.Json;

static ChatRouteCatalog LoadLiteLlm(JsonDocument doc)
{
    var routes = new List<ChatRoute>();
    foreach (JsonProperty model in doc.RootElement.EnumerateObject())
    {
        if (model.Name == "sample_spec")
        {
            continue;
        }

        JsonElement v = model.Value;
        routes.Add(new ChatRoute(
            name: model.Name,
            providerName: v.TryGetProperty("litellm_provider", out var p) ? p.GetString() : null,
            modelId: model.Name,
            maxInputTokens: v.TryGetProperty("max_input_tokens", out var mit) ? mit.GetInt32() : null,
            inputTokenCostPerMillion: v.TryGetProperty("input_cost_per_token", out var ic)
                ? ic.GetDecimal() * 1_000_000 : null,
            outputTokenCostPerMillion: v.TryGetProperty("output_cost_per_token", out var oc)
                ? oc.GetDecimal() * 1_000_000 : null,
            sourceUri: new Uri("https://github.com/BerriAI/litellm")));
    }

    return new ChatRouteCatalog(routes);
}

// Bind the entries you want to a router:
ChatRouteCatalog catalog = LoadLiteLlm(JsonDocument.Parse(json));

IChatClient router = new OrderedFailoverClient(
[
    catalog.Get("gpt-4o-mini").WithClient(openai),
    catalog.Get("claude-3-5-sonnet").WithClient(anthropic),
]);
```

---

## 4. More archetypes

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
| Try routes **in order until one works** | an ordered-failover `RoutingChatClient` subclass (section 1) |
| **Pin** a request to a specific model/route | `ChatOptions.ModelId` (read in your `SelectNextRouteAsync`) |
| Route across **different providers/clients** | routes bound via `client:` / `WithClient` |
| **Cheapest that fits** | a `RoutingChatClient` subclass reading `ChatRoute` cost/context metadata |
| **Fall back** after a failure | later `SelectNextRouteAsync` calls (branch on `lastException`) |
| **Prune a provider** on an auth error | interpret `lastException`, don't return that provider's routes |
| **Skip** a rate-limited/unhealthy route | don't return it (keep your own cooldown state) |
| Route by **difficulty / meaning** | a `RoutingChatClient` subclass that classifies on the first call |
| **Nested** region/tenant → model routing | a route whose `Client` is another `RoutingChatClient` |
| Reuse **model metadata** | `ChatRouteCatalog` (+ your JSON loader) bound with `WithClient` |
