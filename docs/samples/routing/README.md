# RoutingChatClient samples

Illustrative `RoutingChatClient` subclasses referenced by
[../routing-chat-client-cookbook.md](../routing-chat-client-cookbook.md). Each file is a complete policy —
one override of `SelectRouteAsync` — that you can copy into your own project and adapt.

These files are **documentation samples**, not a shipped library: they are not part of the build, and the
routing types they use are experimental (`[Experimental("MEAI001")]`), so each file opens with
`#pragma warning disable MEAI001`.

| File | Policy | Cookbook section |
|---|---|---|
| [ComplexityRoutingClient.cs](./ComplexityRoutingClient.cs) | Rule-based complexity tiering (no model call) | Route by difficulty |
| [SemanticRoutingClient.cs](./SemanticRoutingClient.cs) | Embedding similarity against per-route utterances | Route by meaning |
| [CapabilityGatingClient.cs](./CapabilityGatingClient.cs) | Require tools / vision / structured output the request needs | Require a capability |
| [StickyRoutingClient.cs](./StickyRoutingClient.cs) | App-owned conversation pinning over an inner policy | Sticky sessions |
| [CooldownRoutingClient.cs](./CooldownRoutingClient.cs) | Skip rate-limited routes until they cool | Cooldown |
| [CircuitBreakerRoutingClient.cs](./CircuitBreakerRoutingClient.cs) | Open a route's circuit after repeated failures | Circuit breaker |
| [OrderedFailoverClient.cs](./OrderedFailoverClient.cs) | Try routes in order until one succeeds | Ordered failover |
| [CheapestRouteClient.cs](./CheapestRouteClient.cs) | Cheapest route that fits the context window | Cheapest that fits |
