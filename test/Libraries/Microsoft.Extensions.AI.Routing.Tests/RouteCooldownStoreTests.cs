// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Xunit;

namespace Microsoft.Extensions.AI;

public class RouteCooldownStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsCooled_WithinWindow_ReturnsTrue()
    {
        DateTimeOffset now = _t0;
        var store = new RouteCooldownStore(() => now);

        store.Cool("a", TimeSpan.FromSeconds(10));

        now = _t0.AddSeconds(5);
        Assert.True(store.IsCooled("a"));
    }

    [Fact]
    public void IsCooled_AfterWindowExpires_ReturnsFalse()
    {
        DateTimeOffset now = _t0;
        var store = new RouteCooldownStore(() => now);

        store.Cool("a", TimeSpan.FromSeconds(10));

        now = _t0.AddSeconds(11);
        Assert.False(store.IsCooled("a"));
    }

    [Fact]
    public void IsCooled_AtExactExpiry_ReturnsFalse()
    {
        DateTimeOffset now = _t0;
        var store = new RouteCooldownStore(() => now);

        store.Cool("a", TimeSpan.FromSeconds(10));

        // The window is half-open: the route is cooled while now < until, so it is eligible again at the instant it expires.
        now = _t0.AddSeconds(10);
        Assert.False(store.IsCooled("a"));
    }

    [Fact]
    public void IsCooled_UnknownRoute_ReturnsFalse()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.False(store.IsCooled("never-cooled"));
    }

    [Fact]
    public void RouteNames_AreCaseInsensitive()
    {
        DateTimeOffset now = _t0;
        var store = new RouteCooldownStore(() => now);

        store.Cool("Gpt-4o", TimeSpan.FromSeconds(10));

        Assert.True(store.IsCooled("gpt-4o"));
        Assert.True(store.Clear("GPT-4O"));
        Assert.False(store.IsCooled("Gpt-4o"));
    }

    [Fact]
    public void Cool_ReplacesExistingWindowRatherThanExtending()
    {
        DateTimeOffset now = _t0;
        var store = new RouteCooldownStore(() => now);

        store.Cool("a", TimeSpan.FromSeconds(100));
        store.Cool("a", TimeSpan.FromSeconds(5));

        now = _t0.AddSeconds(10);
        Assert.False(store.IsCooled("a"));
    }

    [Fact]
    public void Cool_NonPositiveDuration_NeverCools()
    {
        var store = new RouteCooldownStore(() => _t0);

        store.Cool("a", TimeSpan.Zero);
        Assert.False(store.IsCooled("a"));

        store.Cool("b", TimeSpan.FromSeconds(-5));
        Assert.False(store.IsCooled("b"));
    }

    [Fact]
    public void Clear_RemovesCooldown_AndReportsWhetherPresent()
    {
        var store = new RouteCooldownStore(() => _t0);
        store.Cool("a", TimeSpan.FromSeconds(100));

        Assert.True(store.Clear("a"));
        Assert.False(store.IsCooled("a"));

        // A second clear finds nothing to remove.
        Assert.False(store.Clear("a"));
    }

    [Fact]
    public void Clear_UnknownRoute_ReturnsFalse()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.False(store.Clear("never-cooled"));
    }

    [Fact]
    public void DefaultClock_UsesUtcNow()
    {
        var store = new RouteCooldownStore();

        store.Cool("a", TimeSpan.FromMinutes(5));

        Assert.True(store.IsCooled("a"));
    }

    [Fact]
    public void Cool_NullRoute_Throws()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.Throws<ArgumentNullException>(() => store.Cool(null!, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Cool_EmptyRoute_Throws()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.Throws<ArgumentException>(() => store.Cool(string.Empty, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IsCooled_EmptyRoute_Throws()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.Throws<ArgumentException>(() => store.IsCooled(string.Empty));
    }

    [Fact]
    public void Clear_EmptyRoute_Throws()
    {
        var store = new RouteCooldownStore(() => _t0);

        Assert.Throws<ArgumentException>(() => store.Clear(string.Empty));
    }
}
