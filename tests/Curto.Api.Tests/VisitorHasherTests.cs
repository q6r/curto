using System.Net;
using Curto.Api.Services;

namespace Curto.Api.Tests;

public sealed class VisitorHasherTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void Same_visitor_on_the_same_day_gets_the_same_id()
    {
        var hasher = new VisitorHasher();

        Assert.Equal(
            hasher.Hash(Client, "UA/1", Noon),
            hasher.Hash(Client, "UA/1", Noon.AddHours(9)));
    }

    [Fact]
    public void Different_address_or_user_agent_gives_a_different_id()
    {
        var hasher = new VisitorHasher();
        var baseline = hasher.Hash(Client, "UA/1", Noon);

        Assert.NotEqual(baseline, hasher.Hash(IPAddress.Parse("203.0.113.8"), "UA/1", Noon));
        Assert.NotEqual(baseline, hasher.Hash(Client, "UA/2", Noon));
    }

    [Fact]
    public void Salt_rotates_at_utc_midnight()
    {
        var hasher = new VisitorHasher();
        var lastSecond = new DateTimeOffset(2026, 3, 15, 23, 59, 59, TimeSpan.Zero);
        var firstSecond = lastSecond.AddSeconds(1);

        Assert.NotEqual(hasher.Hash(Client, "UA/1", lastSecond), hasher.Hash(Client, "UA/1", firstSecond));
    }

    [Fact]
    public void The_day_is_decided_in_utc_whatever_the_offset_of_the_instant()
    {
        var hasher = new VisitorHasher();
        var utc = new DateTimeOffset(2026, 3, 15, 1, 0, 0, TimeSpan.Zero);
        var sameInstantInSaoPaulo = utc.ToOffset(TimeSpan.FromHours(-3)); // still 14 March locally

        Assert.Equal(hasher.Hash(Client, "UA/1", utc), hasher.Hash(Client, "UA/1", sameInstantInSaoPaulo));
    }

    [Fact]
    public void A_restart_forgets_the_salt_so_old_ids_cannot_be_recomputed()
    {
        var before = new VisitorHasher().Hash(Client, "UA/1", Noon);
        var after = new VisitorHasher().Hash(Client, "UA/1", Noon);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Late_arrivals_around_midnight_share_the_newer_salt()
    {
        var hasher = new VisitorHasher();
        var tomorrow = hasher.Hash(Client, "UA/1", Noon.AddDays(1));

        // A request stamped before midnight but processed after the rotation must not bring yesterday's salt back.
        Assert.Equal(tomorrow, hasher.Hash(Client, "UA/1", Noon));
        Assert.Equal(tomorrow, hasher.Hash(Client, "UA/1", Noon.AddDays(1)));
    }

    [Fact]
    public void Missing_inputs_hash_as_empty_strings()
    {
        var hasher = new VisitorHasher();

        Assert.Equal(hasher.Hash(null, null, Noon), hasher.Hash(null, string.Empty, Noon));
        Assert.NotEqual(hasher.Hash(null, null, Noon), hasher.Hash(Client, null, Noon));
    }

    [Fact]
    public void Address_and_user_agent_cannot_be_shifted_into_each_other()
    {
        var hasher = new VisitorHasher();

        // "1.2.3.4" + NUL + "5" and "1.2.3.45" + NUL + "" would collide without the separator.
        Assert.NotEqual(
            hasher.Hash(IPAddress.Parse("1.2.3.4"), "5", Noon),
            hasher.Hash(IPAddress.Parse("1.2.3.45"), string.Empty, Noon));
    }

    [Fact]
    public async Task Concurrent_callers_agree_on_one_salt_per_day()
    {
        var hasher = new VisitorHasher();

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => hasher.Hash(Client, "UA/1", Noon))));

        Assert.Single(results.Distinct());
    }
}
