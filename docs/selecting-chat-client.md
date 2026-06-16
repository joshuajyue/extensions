# SelectingChatClient composition and stickiness modes

## Current semantics (as implemented today)

`SelectingChatClient` is currently a **routing/composition root**, not ordinary pass-through middleware.

1. You create provider/model-specific `IChatClient` instances first.
2. You create one `SelectingChatClient` with those candidates and a selector.
3. You add cross-cutting middleware on top by calling `.AsBuilder().UseX().Build()`.

There is currently no built-in `UseSelection(...)` extension method in `Microsoft.Extensions.AI`.

## Why this shape

`SelectingChatClient` chooses a candidate and forwards directly to that candidate client.  
That means model selection happens at the root/client-composition boundary, and middleware is applied after that root is created.

## Proposed stickiness API (simplified)

For this scenario, the most sensible public API is an enum:

- `EveryCall`
- `PerInstance`
- `ByConversationId`

`ByConversationId` is the only sticky key needed for v1 chat scenarios.
If `ChatOptions.ConversationId` is missing, behavior should fall back to a defined default (for example `EveryCall`).

State storage used by stickiness (maps/caches) should be internal implementation detail, not user-facing.

## Example: two models + sticky by `ConversationId`

```csharp
using Microsoft.Extensions.AI;

// Provider/model clients created by your app (OpenAI, Azure OpenAI, etc.).
IChatClient gpt53Client = ...;
IChatClient gpt4oMiniClient = ...;

// Build selection root directly.
// No selector is supplied here, so the current built-in placeholder selection logic is used
// (today that default picks the first candidate).
IChatClient root = new SelectingChatClient(
    candidates:
    [
        new SelectingChatClientCandidate("gpt-5.3", gpt53Client, providerName: "openai", modelId: "gpt-5.3"),
        new SelectingChatClientCandidate("gpt-4o-mini", gpt4oMiniClient, providerName: "openai", modelId: "gpt-4o-mini"),
    ],
    stickiness: SelectingChatClient.SelectionStickiness.ByConversationId);

// Middleware is layered after selection root creation.
IChatClient client = root
    .AsBuilder()
    .UseFunctionInvocation()
    .UseOpenTelemetry()
    .Build();

// ConversationId enables sticky model reuse per conversation.
var options = new ChatOptions { ConversationId = "conv-123" };
var response = await client.GetResponseAsync(messages, options);
```

## Future TODO

1. Document recommended ordering with other middleware (function invocation, telemetry, logging, caching).
2. Add end-to-end samples covering single-provider multi-model selection, multi-provider selection, and sticky plus fallback behavior.
3. Replace the placeholder default selection logic (first candidate) with production selection policy logic.
