// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Extensions.AI;

// End-to-end availability coverage: a RouteCooldownStore read from the router's canRoute predicate expresses
// route health as a candidate filter. Because canRoute narrows the ONE candidate set that both the selector and
// the fallback walk observe, a single recipe covers both — there is no separate "gate the selector AND prune in
// onFailure" contract to keep in sync.
public class RouteAvailabilityTests
{
    [Fact]
    public async Task CanRoute_RemovesCooledRoute_FromSelectorCandidates()
    {
        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("b", TimeSpan.FromMinutes(5));

        IReadOnlyList<ChatRoute>? seen = null;
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx =>
        {
            seen = ctx.Routes;
            return new ChatRoutePlan(ctx.Routes[0]);
        });

        using var a = Responds("a");
        using var b = Responds("b");
        using var c = Responds("c");
        using var router = new RoutingChatClient(
            [new("a", client: a), new("b", client: b), new("c", client: c)],
            selector,
            canRoute: (route, _, _) => !cooldowns.IsCooled(route.Name));

        ChatResponse response = await router.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(["a", "c"], seen!.Select(r => r.Name));
        Assert.Equal("a", response.Text);
    }

    [Fact]
    public async Task CanRoute_AllCooled_FallsThroughToFullSet()
    {
        // The availability filter is soft: rather than strand the request when every route is cooling, the router
        // falls back to the full candidate set so the selector still produces a plan.
        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("a", TimeSpan.FromMinutes(5));
        cooldowns.Cool("b", TimeSpan.FromMinutes(5));

        IReadOnlyList<ChatRoute>? seen = null;
        IChatRouteSelector selector = ChatRouteSelector.Create(ctx =>
        {
            seen = ctx.Routes;
            return new ChatRoutePlan(ctx.Routes[0]);
        });

        using var a = Responds("a");
        using var b = Responds("b");
        using var router = new RoutingChatClient(
            [new("a", client: a), new("b", client: b)],
            selector,
            canRoute: (route, _, _) => !cooldowns.IsCooled(route.Name));

        await router.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(["a", "b"], seen!.Select(r => r.Name));
    }

    [Fact]
    public async Task CanRoute_FiltersFallbackWalk_SoNaiveOnFailureSkipsCooledRoute()
    {
        // The single-place contract: because canRoute narrows the candidate set that the fallback walk draws from,
        // a cooled route never appears in RouteFailureContext.Remaining. Even a naive onFailure that returns
        // ctx.Remaining verbatim — with no knowledge of the cooldown store — skips the cooling route.
        int cooledCalls = 0;
        using TestChatClient flaky = Throws();
        using TestChatClient cooled = Records(() => cooledCalls++, "cooled");
        using TestChatClient healthy = Responds("ok");

        var cooldowns = new RouteCooldownStore();
        cooldowns.Cool("cooled", TimeSpan.FromMinutes(5));

        IChatRouteSelector selector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "flaky")));

        using var router = new RoutingChatClient(
            [new("flaky", client: flaky), new("cooled", client: cooled), new("healthy", client: healthy)],
            selector,
            onFailure: ctx => ctx.Remaining, // naive: does NOT consult the cooldown store
            canRoute: (route, _, _) => !cooldowns.IsCooled(route.Name));

        ChatResponse response = await router.GetResponseAsync([new(ChatRole.User, "hi")]);

        Assert.Equal(0, cooledCalls);       // the cooling route was never attempted
        Assert.Equal("ok", response.Text);  // the healthy route served the request
    }

    [Fact]
    public async Task CanRoute_WithSticky_CooledPin_Defers_ThenReattachesAfterCooldown()
    {
        // Layered with StickyChatRouteSelector: canRoute drops a cooling route from the candidates, so a pin to it
        // cannot resolve and the base policy is used instead; once the cooldown expires the pin re-attaches.
        DateTimeOffset clock = DateTimeOffset.UtcNow;
        var cooldowns = new RouteCooldownStore(() => clock);

        IChatRouteSelector baseSelector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(Route(ctx, "backup")));
        var sticky = new StickyChatRouteSelector(_ => ["primary"], baseSelector);

        using var primary = Responds("primary");
        using var backup = Responds("backup");
        using var router = new RoutingChatClient(
            [new("primary", client: primary), new("backup", client: backup)],
            sticky,
            canRoute: (route, _, _) => !cooldowns.IsCooled(route.Name));

        // Healthy: the pin resolves to primary.
        Assert.Equal("primary", (await router.GetResponseAsync([new(ChatRole.User, "hi")])).Text);

        // Cool primary: it drops out of candidates, the pin cannot resolve, the base policy picks backup.
        cooldowns.Cool("primary", TimeSpan.FromMinutes(5));
        Assert.Equal("backup", (await router.GetResponseAsync([new(ChatRole.User, "hi")])).Text);

        // After the cooldown expires, the pin re-attaches automatically — no explicit un-pin step.
        clock += TimeSpan.FromMinutes(10);
        Assert.Equal("primary", (await router.GetResponseAsync([new(ChatRole.User, "hi")])).Text);
    }

    [Fact]
    public async Task CanRoute_StickyPin_CooledOnFailure_ThenReattachesAfterWindow()
    {
        // The full stickiness <-> onFailure loop across three turns, driven through the real RoutingChatClient with
        // availability expressed once in canRoute:
        //   1. The pinned route fails; onFailure cools it and the request falls over to the backup THIS turn.
        //   2. Next turn canRoute removes the cooled route from the candidates, so the pin cannot resolve and the
        //      base policy serves from the backup WITHOUT ever attempting the cooled route.
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

        IChatRouteSelector baseSelector = ChatRouteSelector.Create(ctx => new ChatRoutePlan(ctx.Routes[0]));
        var sticky = new StickyChatRouteSelector(_ => ["primary"], baseSelector);

        using var router = new RoutingChatClient(
            [new("primary", client: primary), new("backup", client: backup)],
            sticky,
            onFailure: ctx =>
            {
                cooldowns.Cool(ctx.Route.Name, TimeSpan.FromMinutes(5)); // write: cool the failed route
                return ctx.Remaining;                                   // canRoute already filtered the cooled route out
            },
            canRoute: (route, _, _) => !cooldowns.IsCooled(route.Name));

        // Turn 1: the pin resolves to primary, primary 429s, onFailure cools it and fails over to backup.
        ChatResponse turn1 = await router.GetResponseAsync([new(ChatRole.User, "hi")]);
        Assert.Equal("backup", turn1.Text);
        Assert.Equal("backup", turn1.AdditionalProperties![RoutingChatClient.SelectedRouteNameKey]);
        Assert.Equal(1, primaryCalls); // primary was attempted exactly once — the failing attempt

        // Turn 2: primary is still cooling, so canRoute drops it, the pin cannot resolve, and the base policy serves
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
