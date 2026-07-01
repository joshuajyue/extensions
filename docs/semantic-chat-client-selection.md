# SemanticChatRouteSelector: embedding-based routing

> `SemanticChatRouteSelector` is an experimental (`MEAI001`) selection policy for
> [`RoutingChatClient`](routing-chat-client.md).
> It ships in the experimental `Microsoft.Extensions.AI.Routing` package while staying in the
> `Microsoft.Extensions.AI` namespace.

## Overview

`SemanticChatRouteSelector` is an `IChatRouteSelector` that routes by **semantic similarity**. You give
each model a small set of representative example phrases — its *profile* utterances. At request time the
selector embeds the user's message, measures how close it is to each model's utterances, and routes to
the closest-matching model. It needs only an `IEmbeddingGenerator<string, Embedding<float>>` — no extra
LLM classification call.

It is a drop-in policy: the routing **mechanism** (`RoutingChatClient`) is unchanged; only the
selector differs. It implements Aurelio Labs' `semantic-router` routing algorithm, the same library
LiteLLM's "auto router" delegates to.

## How it works (a walk through the code)

Everything happens in [`SemanticChatRouteSelector.cs`](../src/Libraries/Microsoft.Extensions.AI.Routing/SemanticChatRouteSelector.cs).
The public entry point is `SelectRouteAsync(ChatRouteContext context, …)`, which runs these steps:

### 1. Build the constructor state

The constructor copies `modelProfiles` into a case-insensitive `Dictionary<string, string[]>`
(`_modelProfiles`), validating that every model has at least one non-blank utterance. It also unpacks
`SemanticRouterOptions` into plain fields — `_topK`, `_aggregation`, `_scoreThreshold`,
`_scoreThresholdByModel` — and stores the optional `_defaultModel`. Nothing is embedded yet.

### 2. Extract the query text — `GetLastUserText`

`SelectRouteAsync` calls `GetLastUserText(ctx.Messages)`, which loops the messages and keeps the **last
message whose role is `User`** (assistant/system/tool messages are ignored). If there is no user
message, `query` is null/blank and the method short-circuits: it returns a plan built from
`PrimaryFallback` (see step 7) — there is no text to route on.

### 3. Lazily embed the profiles — `EnsureProfilesAsync` / `EmbedProfilesAsync`

`EnsureProfilesAsync` builds the profile index **once** and caches it in `_profilesTask`, guarded by a
lock and `Volatile.Read`. A faulted or canceled embedding task is not cached, so a transient failure
is retried on the next request. `EmbedProfilesAsync` flattens every `(model → utterances)` pair into two
parallel lists — `names[i]` is the model that owns `utterances[i]` — embeds all utterances in a single
`GenerateAsync` call, and stores the results in a `ProfileIndex`:

```csharp
private sealed class ProfileIndex
{
    public string[] RouteNames { get; }  // RouteNames[i] owns Vectors[i]
    public float[][] Vectors { get; }    // one embedding per utterance
}
```

### 4. Embed the query and score every utterance

Back in `SelectRouteAsync`, the query is embedded with one `GenerateAsync([query])` call, giving
`queryVector`. The code then builds a `HashSet` of the **currently registered** model names (from
`ctx.Models`) so utterances belonging to a model that is no longer registered are skipped. For each
remaining utterance it computes cosine similarity and records a `Match`:

```csharp
double score = TensorPrimitives.CosineSimilarity(queryVector.Span, index.Vectors[i]);
matches.Add(new Match(route, score, matches.Count));   // route = owning model, Order = tiebreak
```

`TensorPrimitives.CosineSimilarity` (from `System.Numerics.Tensors`) is `dot(a,b) / (‖a‖·‖b‖)`, so
the vectors do **not** need to be pre-normalized.

### 5. Keep the global top-K — `matches.Sort` + `take`

The full `matches` list is sorted by **descending score**, breaking ties by original `Order` (so the
result is deterministic). Then `take = Min(_topK, matches.Count)` — only the globally highest `_topK`
utterance matches survive. This shortlist is **global across all models**, not per model: a model must
land an utterance in the overall top-K to be considered at all.

### 6. Aggregate per model — `AggregateTopMatches`

`AggregateTopMatches` walks the first `take` matches and groups them by model into running
`(Sum, Max, Count)` tuples. It then produces one score per model according to `_aggregation`:

```csharp
SemanticRouteAggregation.Sum => stats.Sum,
SemanticRouteAggregation.Max => stats.Max,
_                            => stats.Sum / stats.Count,   // Mean (default)
```

Models with no surviving match get no entry (they scored nothing in the shortlist).

### 7. Pick the winner — `SelectWinner`, else `PrimaryFallback`

`SelectWinner` sorts the aggregated models by descending score (ties by registration order) and returns
the **first model whose score meets its threshold**. The threshold is the per-model override from
`_scoreThresholdByModel` if present, otherwise the global `_scoreThreshold` (default `0.3`):

```csharp
float threshold = _scoreThreshold;
if (_scoreThresholdByModel?.TryGetValue(name, out float perModel) == true)
    threshold = perModel;
if (score >= threshold) return name;   // first qualifier wins
```

If nothing meets its threshold, `SelectWinner` returns null and `PrimaryFallback` chooses the primary:
the `_defaultModel` if it is still registered, otherwise `ctx.Models[0]` (the first registered model).

### 8. Build the ordered plan — `OrderPlan`

Finally `OrderPlan` returns a `RoutingChatModel[]` with the **primary first**, then the remaining models
by **descending aggregated score**, with registration order as the final tiebreak (unscored models sort
last). `SelectRouteAsync` wraps that array in a `ChatRoutePlan`, so the lower-scoring models become the
ordered **fallbacks** the `RoutingChatClient` will try if the primary fails.

### End-to-end example

```
User message: "Write a Python function for quicksort"
  -> GetLastUserText -> embed -> queryVector

ProfileIndex (embedded once, cached; each utterance tagged with its model):
  fast:    "docs", "tutorials", "simple Q&A"             -> cosine ~0.55, 0.50, 0.41
  capable: "complex logic", "advanced algorithms", "code" -> cosine ~0.98, 0.91, 0.88

Sort all utterances by score, keep global top-5 -> group by model -> Mean:
  capable ~0.92    fast ~0.49

SelectWinner (threshold 0.3, descending): capable 0.92 >= 0.3  -> winner = "capable"
OrderPlan: [ capable (primary), fast (fallback) ]
```

## Usage

```csharp
using Microsoft.Extensions.AI;

// Any IEmbeddingGenerator<string, Embedding<float>>. Wrap it with caching to amortize cost.
IEmbeddingGenerator<string, Embedding<float>> embedder =
    new EmbeddingGeneratorBuilder<string, Embedding<float>>(rawEmbedder)
        .UseCaching()
        .Build();

// Describe each model with a few representative utterances. The key is the model name
// (the same name passed to AddModel / RoutingChatModel).
var profiles = new Dictionary<string, IReadOnlyList<string>>
{
    ["openai:gpt-4o-mini"] = ["documentation", "tutorials", "simple questions", "short answers"],
    ["openai:gpt-5.3"]     = ["complex reasoning", "advanced algorithms", "multi-step code", "architecture"],
};

var selector = new SemanticChatRouteSelector(
    embedder,
    profiles,
    defaultModel: "openai:gpt-4o-mini",          // used when nothing passes the threshold
    options: new SemanticRouterOptions
    {
        TopK = 5,                                 // global top-k utterance matches (LiteLLM integration default)
        Aggregation = SemanticRouteAggregation.Mean, // Mean (default), Sum, or Max
        ScoreThreshold = 0.3f,                    // matches the LiteLLM integration default
    });

IChatClient router = new RoutingChatClientBuilder()
    .AddModel("openai:gpt-4o-mini", gpt4oMiniClient, modelId: "gpt-4o-mini")
    .AddModel("openai:gpt-5.3", gpt53Client, modelId: "gpt-5.3")
    .UseSelector(selector)
    .Build();
```

The selector depends only on `IEmbeddingGenerator<string, Embedding<float>>`, so it works with
any embedding provider (OpenAI, Azure OpenAI, local, custom). Profiles are embedded with the
same generator, so the comparison is apples-to-apples.

## When it makes sense

**Ideal:**
- Large cost differential between models (route cheap by default, escalate when warranted).
- High request volume where the embedding cost amortizes (especially with a caching embedder).
- Diverse workloads where some queries genuinely benefit from a stronger model.

**Not ideal:**
- All models cost roughly the same (the overhead is not justified).
- Ultra-low-latency requirements where any extra embedding call is unacceptable.
- Homogeneous query types, where `ComplexityChatRouteSelector` or the default is sufficient.

## Notes and tuning (`SemanticRouterOptions`)

- **Profile quality matters.** Utterances should be short, representative examples of what each
  model should handle. A handful of focused phrases usually outperforms a single long one.
- **`Aggregation`.** `Mean` (default) rewards a model that matches the query *consistently*; `Max`
  rewards a single strong match; `Sum` rewards matching in *many* ways (and favors models with more
  utterances). These match the aggregation modes used by `semantic-router`/LiteLLM.
- **`TopK`.** Only the globally highest `TopK` utterance matches (default `5`) feed aggregation, so a
  model must place an utterance in that global shortlist to be considered. Lower it to make routing
  more "winner-take-all"; raise it to average over more evidence.
- **`ScoreThreshold` / `ScoreThresholdByModel`.** Cosine similarity ranges from -1 to 1. A model is
  only chosen when its aggregated score meets its threshold; otherwise the `defaultModel` (or first
  registered model) is used. Use a per-model override to hold a specific model to a higher bar. Set a
  threshold of 0 or below to always route to the best-scoring model.
- **Caching.** Profiles are embedded once per selector instance. Wrap the embedder with
  `UseCaching()` so repeated/similar queries also avoid re-embedding.

## Strategy comparison

| Strategy | Implementation | Latency | Setup |
|---|---|---|---|
| Default (pin / first) | `ChatOptions.ModelId` or `Models[0]` | ~0 ms | trivial |
| `ComplexityChatRouteSelector` | deterministic keyword/pattern tiering | ~1 ms | low |
| `SemanticChatRouteSelector` | embedding + cosine similarity | ~embedding call | medium |
| LLM classification | full inference | high | high |
