using Curto.Api.Services;

namespace Curto.Api.Tests;

public sealed class SlugRulesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("ABC")]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("___")]
    [InlineData("---")]
    [InlineData("0123456789")]
    [InlineData("Mixed-Case_123")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 32
    public void Accepts_slugs_of_3_to_32_url_safe_characters(string slug)
    {
        Assert.True(SlugRules.IsValidFormat(slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33
    [InlineData("has space")]
    [InlineData("dot.dot")]
    [InlineData("slash/slash")]
    [InlineData("back\\slash")]
    [InlineData("per%cent")]
    [InlineData("quest?ion")]
    [InlineData("hash#tag")]
    [InlineData("pl+us")]
    [InlineData("café")]
    [InlineData("абв")] // Cyrillic look-alikes
    [InlineData("tab\t")]
    [InlineData("new\nline")]
    [InlineData("nul\0")]
    [InlineData("..")]
    [InlineData("../x")]
    public void Rejects_everything_else(string slug)
    {
        Assert.False(SlugRules.IsValidFormat(slug));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("qr")]
    [InlineData("docs")]
    [InlineData("healthz")]
    [InlineData("readyz")]
    [InlineData("openapi")]
    [InlineData("assets")]
    [InlineData("admin")]
    [InlineData("DOCS")]
    [InlineData("Api")]
    [InlineData("HealthZ")]
    public void Reserved_words_are_matched_ignoring_case(string slug)
    {
        Assert.True(SlugRules.IsReserved(slug));
    }

    [Theory]
    [InlineData("apix")]
    [InlineData("my-docs")]
    [InlineData("qrs")]
    [InlineData("docs1")]
    public void Look_alikes_are_not_reserved(string slug)
    {
        Assert.False(SlugRules.IsReserved(slug));
    }

    [Fact]
    public void The_deny_list_is_clean()
    {
        // Lower-case letters only (matching is case-insensitive) and no duplicates: catches typos and stray whitespace.
        foreach (var word in SlugRules.ReservedWords)
        {
            Assert.Matches("^[a-z]+$", word);
        }

        Assert.Equal(SlugRules.ReservedWords.Count, SlugRules.ReservedWords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Generated_slugs_are_seven_base62_characters()
    {
        var generator = new SlugGenerator();
        HashSet<string> seen = [];

        for (var i = 0; i < 2_000; i++)
        {
            var slug = generator.Next();

            Assert.Equal(SlugRules.GeneratedLength, slug.Length);
            Assert.All(slug, c => Assert.Contains(c, SlugRules.Base62));
            Assert.True(SlugRules.IsValidFormat(slug));
            seen.Add(slug);
        }

        // 62^7 possibilities: 2,000 draws colliding would mean the generator is broken, not unlucky (p ~ 6e-7 per run).
        Assert.True(seen.Count >= 1_999, $"only {seen.Count} distinct slugs out of 2000");
    }

    [Fact]
    public void Generated_slugs_use_the_whole_alphabet()
    {
        var generator = new SlugGenerator();
        HashSet<char> seen = [];

        for (var i = 0; i < 2_000; i++)
        {
            foreach (var c in generator.Next())
            {
                seen.Add(c);
            }
        }

        // 14,000 uniform draws miss a given symbol with probability (61/62)^14000 ~ 1e-98.
        Assert.Equal(SlugRules.Base62.Length, seen.Count);
    }
}
