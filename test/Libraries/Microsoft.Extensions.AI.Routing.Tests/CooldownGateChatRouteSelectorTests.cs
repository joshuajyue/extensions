// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

public class CooldownGateChatRouteSelectorTests
{
    [Fact]
    public void Ctor_NullCooldowns_Throws()
    {
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));

        Assert.Throws<ArgumentNullException>(() => new CooldownGateChatRouteSelector(null!, inner));
    }

    [Fact]
    public void Ctor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CooldownGateChatRouteSelector(new RouteCooldownStore(), null!));
    }

    [Fact]
    public async Task RemovesCooledRoutes_FromInnerCandidates()
    {
        ChatRoute[] routes = [new("a"), new("b"), new("c")];
        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("b", TimeSpan.FromMinutes(5));

        IReadOnlyList<ChatRoute>? seen = null;
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx =>
        {
            seen = ctx.Routes;
            return new ChatRoutePlan(ctx.Routes[0]);
        });
        var gate = new CooldownGateChatRouteSelector(cooldowns, inner);

        ChatRoutePlan plan = await gate.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal(["a", "c"], seen!.Select(r => r.Name));
        Assert.Equal("a", plan.OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task AllCooled_FallsThroughWithFullSet()
    {
        // Rather than strand the request when every route is cooling, the gate defers with the ORIGINAL candidate
        // set so the inner policy still produces a plan (the router's fallback is the last line of defense).
        ChatRoute[] routes = [new("a"), new("b")];
        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("a", TimeSpan.FromMinutes(5));
        cooldowns.Cool("b", TimeSpan.FromMinutes(5));

        IReadOnlyList<ChatRoute>? seen = null;
        IChatRouteSelector inner = ChatRouteSelector.Create(ctx =>
        {
            seen = ctx.Routes;
            return new ChatRoutePlan(ctx.Routes[0]);
        });
        var gate = new CooldownGateChatRouteSelector(cooldowns, inner);

        await gate.SelectRouteAsync(Context(routes, "hi"));

        Assert.Equal(["a", "b"], seen!.Select(r => r.Name));
    }

    [Fact]
    public async Task CooledPinnedRoute_DefersThenReattaches()
    {
        // Layered with StickyChatRouteSelector: the gate removes a cooling route from the candidate set, so a pin
        // to it cannot resolve and the base policy is used instead; once the cooldown expires the pin re-attaches.
        ChatRoute[] routes = [new("primary"), new("backup")];
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        var cooldowns = new RouteCooldownStore(() => clock);

        IChatRouteSelector baseSelector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "backup")));
        var sticky = new StickyChatRouteSelector(_ => ["primary"], baseSelector);
        var gate = new CooldownGateChatRouteSelector(cooldowns, sticky);

        // Healthy: the pin resolves to primary.
        Assert.Equal("primary", (await gate.SelectRouteAsync(Context(routes, "hi"))).OrderedRoutes[0].Name);

        // Cool primary: it drops out of candidates, the pin cannot resolve, the base policy picks backup.
        cooldowns.Cool("primary", TimeSpan.FromMinutes(5));
        Assert.Equal("backup", (await gate.SelectRouteAsync(Context(routes, "hi"))).OrderedRoutes[0].Name);

        // After the cooldown expires, the pin re-attaches automatically — no explicit un-pin step.
        clock += TimeSpan.FromMinutes(10);
        Assert.Equal("primary", (await gate.SelectRouteAsync(Context(routes, "hi"))).OrderedRoutes[0].Name);
    }

    [Fact]
    public async Task CooldownAwareFallback_SkipsCooledRoute()
    {
        // The "two-place" contract: gating the selector is not enough because the router's onFailure lookahead
        // spans its OWN registered routes, not just what the selector saw. The cooldown must ALSO be applied in the
        // onFailure delegate; here it is, and the cooled route is never hit.
        int cooledCalls = 0;
        using TestChatClient flaky = Throws();
        using TestChatClient cooled = Records(() => cooledCalls++, "cooled");
        using TestChatClient healthy = Responds("ok");
        ChatRoute[] routes =
        [
            new("flaky", client: flaky),
            new("cooled", client: cooled),
            new("healthy", client: healthy),
        ];

        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("cooled", TimeSpan.FromMinutes(5));

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "flaky")));
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?> onFailure =
            ctx => ctx.Remaining.Where(r => !cooldowns.IsCooled(r.Name)).ToList();

        using var router = new RoutingChatClient(routes, selector, onFailure, capabilityDetector: (_, _) => Array.Empty<string>());

        ChatResponse response = await router.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(0, cooledCalls);       // the cooling route was never attempted
        Assert.Equal("ok", response.Text);  // the healthy route served the request
    }

    [Fact]
    public async Task NaiveFallback_StillAttemptsCooledRoute()
    {
        // The hazard the other half of the two-place contract guards against: filtering only in the selector is NOT
        // enough. With an onFailure delegate that does not drop cooled routes, the router re-introduces the cooling
        // route and serves the request from it.
        int cooledCalls = 0;
        using TestChatClient flaky = Throws();
        using TestChatClient cooled = Records(() => cooledCalls++, "cooled");
        using TestChatClient healthy = Responds("ok");
        ChatRoute[] routes =
        [
            new("flaky", client: flaky),
            new("cooled", client: cooled),
            new("healthy", client: healthy),
        ];

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "flaky")));
        Func<RouteFailureContext, IReadOnlyList<ChatRoute>?> naiveOnFailure =
            ctx => ctx.Remaining; // does not consult the cooldown store

        using var router = new RoutingChatClient(routes, selector, naiveOnFailure, capabilityDetector: (_, _) => Array.Empty<string>());

        ChatResponse response = await router.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(1, cooledCalls);           // the cooling route WAS attempted
        Assert.Equal("cooled", response.Text);  // and even served the request
    }

    [Fact]
    public async Task StickyPin_ReleasedOnFailure_ThenReattachesAfterCooldown()
    {
        // The full stickiness <-> onFailure loop across three turns, driven through the real RoutingChatClient with
        // the recommended precedence (capability gate -> cooldown gate -> sticky -> base):
        //   1. The pinned route fails; onFailure cools it and the request falls over to the backup THIS turn.
        //   2. Next turn the cooldown gate removes the cooled route from the candidates, so the pin cannot resolve
        //      and the base policy serves from the backup WITHOUT ever attempting the cooled route.
        //   3. Once the window elapses (and the route has recovered) the pin re-attaches and the route serves again.
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        var cooldowns = new RouteCooldownStore(() => clock);

        int primaryCalls = 0;
        bool primaryDown = true;
        using var primary = new TestChatClient
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                primaryCalls++;
                return primaryDown
                    ? throw new InvalidOperationException("429 too many requests")
                    : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "primary")));
            },
        };
        using var backup = Responds("backup");
        ChatRoute[] routes = [new("primary", client: primary), new("backup", client: backup)];

        // The base policy prefers whatever candidate survives the gate; the pin is what forces "primary".
        IChatRouteSelector baseSelector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));
        var selector = new CooldownGateChatRouteSelector(cooldowns, new StickyChatRouteSelector(_ => ["primary"], baseSelector));

        using var router = new RoutingChatClient(
            routes,
            selector,
            onFailure: ctx =>
            {
                cooldowns.Cool(ctx.Route.Name, TimeSpan.FromMinutes(5));               // write: cool the failed route
                return ctx.Remaining.Where(r => !cooldowns.IsCooled(r.Name)).ToList(); // and fall off it this turn
            },
            capabilityDetector: (_, _) => Array.Empty<string>());

        // Turn 1: the pin resolves to primary, primary 429s, onFailure cools it and fails over to backup.
        ChatResponse turn1 = await router.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Equal("backup", turn1.Text);
        Assert.Equal("backup", turn1.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal(1, primaryCalls); // primary was attempted exactly once — the failing attempt

        // Turn 2: primary is still cooling, so the gate drops it, the pin cannot resolve, and the base policy serves
        // from backup — primary is never even attempted this turn.
        ChatResponse turn2 = await router.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Equal("backup", turn2.Text);
        Assert.Equal(1, primaryCalls); // unchanged: the released pin spared the cooling route entirely

        // The route recovers and the cooldown window elapses.
        primaryDown = false;
        clock += TimeSpan.FromMinutes(10);

        // Turn 3: primary is a candidate again, the pin re-attaches automatically, and primary serves the turn.
        ChatResponse turn3 = await router.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Equal("primary", turn3.Text);
        Assert.Equal("primary", turn3.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal(2, primaryCalls); // attempted again and succeeded
    }

    private static ChatRouteContext Context(IReadOnlyList<ChatRoute> routes, string userText) =>
        new([new(ChatRole.User, userText)], options: null, routes);

    private static ChatRoute Route(ChatRouteContext context, string name) =>
        context.Routes.First(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private static TestChatClient Throws() =>
        new() { GetResponseAsyncCallback = (_, _, _) => throw new InvalidOperationException("simulated HTTP 500") };

    private static TestChatClient Responds(string text) =>
        new() { GetResponseAsyncCallback = (_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))) };

    private static TestChatClient Records(Action onCall, string text) =>
        new()
        {
            GetResponseAsyncCallback = (_, _, _) =>
            {
                onCall();
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            },
        };
}
