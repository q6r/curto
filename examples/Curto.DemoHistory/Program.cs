using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Curto.DemoHistory;

/// <summary>
/// Backdates the links of a demo database and fills it with a plausible click history, so that charts, comparisons and
/// sparklines have something to show. Visits made through the HTTP API are always stamped "now", which is why this
/// tool writes straight into the SQLite file: use it on throw-away demo data only, never on a database that matters.
/// </summary>
internal static class Program
{
    private const string Usage =
        "Usage: dotnet run --project examples/Curto.DemoHistory -- --db <path to curto.db> [--days 90] [--clicks-per-day 160] [--seed 2026]";

    private static readonly (string Browser, string Os, string Device, int Weight)[] Clients =
    [
        ("Chrome", "Windows", "desktop", 24), ("Safari", "iOS", "mobile", 17), ("Chrome", "Android", "mobile", 13),
        ("Safari", "macOS", "desktop", 8), ("Chrome", "macOS", "desktop", 7), ("Edge", "Windows", "desktop", 6),
        ("Firefox", "Linux", "desktop", 4), ("Firefox", "Windows", "desktop", 3), ("Samsung Internet", "Android", "mobile", 3),
        ("Safari", "iOS", "tablet", 3), ("Chrome", "Linux", "desktop", 2), ("Bot", "Other", "bot", 2),
    ];

    private static readonly (string? Host, int Weight)[] DefaultReferrers =
    [
        (null, 30), ("news.ycombinator.com", 14), ("t.co", 12), ("linkedin.com", 10), ("reddit.com", 9),
        ("google.com", 8), ("github.com", 6), ("lobste.rs", 4), ("bsky.app", 4), ("duckduckgo.com", 3),
    ];

    private static int Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return Apply(options, DateTimeOffset.UtcNow);
        }
        catch (SqliteException exception)
        {
            Console.Error.WriteLine($"SQLite refused the operation: {exception.Message}");
            return 1;
        }
    }

    private static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new Options(string.Empty, 90, 160, 2026);
        error = string.Empty;

        for (var i = 0; i < args.Length; i += 2)
        {
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "--db":
                    options = options with { Db = value };
                    break;
                case "--days" when int.TryParse(value, CultureInfo.InvariantCulture, out var days) && days is >= 7 and <= 365:
                    options = options with { Days = days };
                    break;
                case "--clicks-per-day" when int.TryParse(value, CultureInfo.InvariantCulture, out var perDay) && perDay is >= 1 and <= 5000:
                    options = options with { ClicksPerDay = perDay };
                    break;
                case "--seed" when int.TryParse(value, CultureInfo.InvariantCulture, out var seed):
                    options = options with { Seed = seed };
                    break;
                default:
                    error = $"Unknown or invalid argument: {args[i]} {value}".TrimEnd();
                    return false;
            }
        }

        if (options.Db.Length == 0)
        {
            error = "--db is required.";
            return false;
        }

        return true;
    }

    private static int Apply(Options options, DateTimeOffset now)
    {
        if (!File.Exists(options.Db))
        {
            Console.Error.WriteLine($"No database at {options.Db}. Start curto once with that CURTO_DB_PATH (it creates the schema), then run this tool.");
            return 1;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.Db, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000;");

        if (Scalar(connection, "PRAGMA user_version;") != 1)
        {
            Console.Error.WriteLine("This is not a curto database at schema version 1.");
            return 1;
        }

        var cutoff = now.AddHours(-36).ToUnixTimeSeconds();
        if (Scalar(connection, "SELECT COUNT(*) FROM clicks WHERE at < $cutoff;", ("$cutoff", cutoff)) > 0)
        {
            Console.Error.WriteLine("This database already has click history. Use a fresh demo database.");
            return 1;
        }

        var links = ReadLinks(connection);
        if (links.Count == 0)
        {
            Console.Error.WriteLine("The database has no links. Run examples/seed.sh or examples/seed.ps1 first.");
            return 1;
        }

        var rng = new Random(options.Seed); // demo data, not security-sensitive
        var plan = Simulate(options, now, links, rng);

        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO clicks (slug, at, visitor, referrer, browser, os, device) VALUES ($slug, $at, $visitor, $referrer, $browser, $os, $device);";
            var slug = insert.Parameters.Add("$slug", SqliteType.Text);
            var at = insert.Parameters.Add("$at", SqliteType.Integer);
            var visitor = insert.Parameters.Add("$visitor", SqliteType.Integer);
            var referrer = insert.Parameters.Add("$referrer", SqliteType.Text);
            var browser = insert.Parameters.Add("$browser", SqliteType.Text);
            var os = insert.Parameters.Add("$os", SqliteType.Text);
            var device = insert.Parameters.Add("$device", SqliteType.Text);
            foreach (var click in plan.Clicks)
            {
                slug.Value = click.Slug;
                at.Value = click.At;
                visitor.Value = click.Visitor;
                referrer.Value = (object?)click.Referrer ?? DBNull.Value;
                browser.Value = click.Browser;
                os.Value = click.Os;
                device.Value = click.Device;
                insert.ExecuteNonQuery();
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE links SET created_at = $created, clicks = clicks + $added WHERE slug = $slug;";
            var slug = update.Parameters.Add("$slug", SqliteType.Text);
            var created = update.Parameters.Add("$created", SqliteType.Integer);
            var added = update.Parameters.Add("$added", SqliteType.Integer);
            foreach (var (name, createdAt, count) in plan.Links)
            {
                slug.Value = name;
                created.Value = createdAt;
                added.Value = count;
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        Console.WriteLine($"Added {plan.Clicks.Count:N0} clicks over {options.Days} days to {plan.Links.Count} of {links.Count} links (seed {options.Seed}).");
        return 0;
    }

    private static Plan Simulate(Options options, DateTimeOffset now, List<LinkRow> links, Random rng)
    {
        // Links that spend a budget or expire keep the shape the API gave them; the others get a history.
        var eligible = links.Where(l => !l.HasBudgetOrExpiry).OrderByDescending(l => l.Clicks).ThenBy(l => l.Slug, StringComparer.Ordinal).ToList();
        var totalWeight = eligible.Sum(l => 1.0 + l.Clicks);
        var firstDay = now.UtcDateTime.Date.AddDays(-(options.Days - 1));
        var hourCdf = HourCumulative();

        List<Click> clicks = [];
        List<(string Slug, long CreatedAt, int Count)> touched = [];

        for (var rank = 0; rank < eligible.Count; rank++)
        {
            var link = eligible[rank];
            var share = (1.0 + link.Clicks) / totalWeight;
            var campaign = rank > 0 && rank % 3 == 0;
            var ageDays = campaign ? 6 + rng.Next(18) : options.Days - 2 - (int)(rank * options.Days * 0.5 / eligible.Count) - rng.Next(5);
            var created = now.UtcDateTime.Date.AddDays(-Math.Clamp(ageDays, 2, options.Days - 1)).AddHours(7 + rng.Next(10)).AddMinutes(rng.Next(60));
            var count = 0;

            for (var day = created.Date; day <= now.UtcDateTime.Date; day = day.AddDays(1))
            {
                var dayIndex = (int)(day - firstDay).TotalDays;
                var age = (day - created.Date).TotalDays;
                var shape = campaign ? (Math.Exp(-age / 9.0) * 3.2) + 0.15 : Math.Min(1.0, 0.4 + (age / 3.0)) * (1.0 + (0.15 * Math.Sin(age / 5.0)));
                var trend = 0.55 + (0.45 * dayIndex / options.Days);
                var weekly = day.DayOfWeek switch { DayOfWeek.Saturday => 0.7, DayOfWeek.Sunday => 0.65, _ => 1.0 };
                var expected = options.ClicksPerDay * share * shape * trend * weekly * (0.8 + (rng.NextDouble() * 0.4));
                var perDay = (int)Math.Round(expected);
                if (perDay == 0)
                {
                    continue;
                }

                // One pool of people per day, shared by every link, sized so that about 1.35 clicks fall on each distinct visitor.
                var visitorsToday = Math.Max(1, (int)Math.Round(1.6 * options.ClicksPerDay * trend * weekly));
                for (var n = 0; n < perDay; n++)
                {
                    var at = PickTime(rng, day, hourCdf);
                    if (at < created || at >= now.UtcDateTime)
                    {
                        continue;
                    }

                    var person = rng.Next(visitorsToday);
                    var id = SplitMix64(((ulong)dayIndex * 1_000_003UL) + (ulong)person + (ulong)options.Seed);
                    var client = Clients[PickIndex(Clients.Select(c => c.Weight).ToArray(), (id >> 11) / (double)(1UL << 53))];
                    var referrers = ReferrersFor(link.Slug);
                    var referrer = referrers[PickIndex(referrers.Select(r => r.Weight).ToArray(), rng.NextDouble())].Host;
                    clicks.Add(new Click(link.Slug, new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds(), (long)id, referrer, client.Browser, client.Os, client.Device));
                    count++;
                }
            }

            touched.Add((link.Slug, new DateTimeOffset(created, TimeSpan.Zero).ToUnixTimeSeconds(), count));
        }

        return new Plan(clicks, touched);
    }

    private static (string? Host, int Weight)[] ReferrersFor(string slug) => slug switch
    {
        "newsletter" => [(null, 70), ("google.com", 6), ("linkedin.com", 8), ("t.co", 6), ("github.com", 4), ("news.ycombinator.com", 6)],
        "github" => [("github.com", 45), (null, 20), ("news.ycombinator.com", 15), ("lobste.rs", 10), ("reddit.com", 10)],
        "podcast" => [(null, 55), ("google.com", 15), ("bsky.app", 10), ("t.co", 10), ("linkedin.com", 10)],
        _ => DefaultReferrers,
    };

    /// <summary>Cumulative share of a day's traffic by UTC hour: an afternoon peak and an evening peak over a low base.</summary>
    private static double[] HourCumulative()
    {
        var weights = Enumerable.Range(0, 24)
            .Select(h => 0.35 + Math.Exp(-Math.Pow(h - 14, 2) / 32.0) + (0.8 * Math.Exp(-Math.Pow(h - 20, 2) / 18.0)))
            .ToArray();
        var total = weights.Sum();
        var running = 0.0;
        return weights.Select(w => running += w / total).ToArray();
    }

    private static DateTime PickTime(Random rng, DateTime day, double[] cumulative)
    {
        var draw = rng.NextDouble();
        var hour = Array.FindIndex(cumulative, c => draw <= c);
        return day.AddHours(Math.Max(0, hour)).AddSeconds(rng.Next(3600));
    }

    private static int PickIndex(int[] weights, double unit)
    {
        var target = unit * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            if (target < weights[i])
            {
                return i;
            }

            target -= weights[i];
        }

        return weights.Length - 1;
    }

    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static List<LinkRow> ReadLinks(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slug, clicks, max_clicks IS NOT NULL OR expires_at IS NOT NULL FROM links;";
        using var reader = command.ExecuteReader();
        List<LinkRow> links = [];
        while (reader.Read())
        {
            links.Add(new LinkRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0));
        }

        return links;
    }

    private static long Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record Options(string Db, int Days, int ClicksPerDay, int Seed);

    private sealed record LinkRow(string Slug, long Clicks, bool HasBudgetOrExpiry);

    private sealed record Click(string Slug, long At, long Visitor, string? Referrer, string Browser, string Os, string Device);

    private sealed record Plan(List<Click> Clicks, List<(string Slug, long CreatedAt, int Count)> Links);
}
