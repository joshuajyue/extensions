# SemanticChatRouteSelector: embedding-based routing

> `SemanticChatRouteSelector` is an experimental (`MEAI001`) selection policy for
> [`RoutingChatClient`](routing-chat-client.md).

## Overview

`SemanticChatRouteSelector` is an `IChatRouteSelector` that routes by **semantic similarity**.
Each model is described by a small set of representative "utterances" (a profile). At request
time the last user message is embedded and the request is routed to the model whose profile is
most similar. This sits between simple rule-based routing and full LLM classification: it gives
semantic understanding using only vector math, with no extra inference call.

It is a drop-in policy: the routing **mechanism** (`RoutingChatClient`) is unchanged; only the
selector differs.

## How it works

1. On first use, all profile utterances are embedded once and cached (keyed by model name).
2. Per request, the last user message is embedded.
3. Each model is scored by the **maximum** cosine similarity between the query and that model's
   utterances, computed with
   `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity`. Models without a profile sort
   last in registration order.
4. Models are ranked by score (highest first; ties preserve registration order). The produced
   `ChatRoutePlan` lists all models, so lower-scoring models become fallbacks.
5. If the best score is below `minimumSimilarity`, or the request has no user text, the first
   registered model is used as the primary route.

```
User message: "Write a Python function for quicksort"
  -> embed -> query vector

Model profiles (embedded once, cached):
  fast:    "documentation, tutorials, simple Q&A"     -> similarity ~0.72
  capable: "complex logic, advanced algorithms, code" -> similarity ~0.98

Decision: route to "capable" (highest similarity)
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
    minimumSimilarity: 0.2f); // optional floor; below it, the first registered model is used

IChatClient router = new RoutingChatClientBuilder()
    .AddModel("openai:gpt-4o-mini", gpt4oMiniClient, modelId: "gpt-4o-mini")
    .AddModel("openai:gpt-5.3", gpt53Client, modelId: "gpt-5.3")
    .UseSelector(selector)
    .UseStickiness(RoutingStickiness.ByConversationId)
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
- Homogeneous query types, where `RuleBasedChatRouteSelector` or the default is sufficient.

## Notes and tuning

- **Profile quality matters.** Utterances should be short, representative examples of what each
  model should handle. A handful of focused phrases usually outperforms a single long one.
- **Caching.** Profiles are embedded once per selector instance. Wrap the embedder with
  `UseCaching()` so repeated/similar queries also avoid re-embedding.
- **`minimumSimilarity`.** Cosine similarity ranges from -1 to 1. Use this floor to send
  ambiguous queries (no profile is a good match) to a safe default — the first registered
  model. The default is `0`.
- **Stickiness.** Combine with `RoutingStickiness.ByConversationId` to keep a conversation on
  one model, and author a `ChatRoutePlan.RemainsValid` predicate (for example, re-embed and
  compare) if you want to re-route when the topic shifts substantially. See
  [routing-chat-client.md](routing-chat-client.md).

## Strategy comparison

| Strategy | Implementation | Latency | Setup |
|---|---|---|---|
| Default (pin / first) | `ChatOptions.ModelId` or `Models[0]` | ~0 ms | trivial |
| `RuleBasedChatRouteSelector` | traits, keywords, length | ~1–5 ms | low |
| `SemanticChatRouteSelector` | embedding + cosine similarity | ~embedding call | medium |
| LLM classification | full inference | high | high |
