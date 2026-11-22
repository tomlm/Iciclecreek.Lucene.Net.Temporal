namespace Iciclecreek.Lucene.Net.Temporal.Tests;

public class DateFieldTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int h = 12) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    private static TestIndex Index() => new TestIndex()
        .CreatedAt("xmas-2024", Utc(2024, 12, 25, 10))
        .CreatedAt("xmas-2025", Utc(2025, 12, 25, 20))
        .CreatedAt("dec-24", Utc(2026, 12, 24))
        .CreatedAt("dec-26", Utc(2026, 12, 26))
        .CreatedAt("seattle-xmas-eve", new DateTimeOffset(2026, 12, 24, 20, 0, 0, TimeSpan.FromHours(-8)), TestIndex.Seattle)
        .CreatedAt("xmas-1999", Utc(1999, 12, 25))
        .CreatedAt("nov-19-morning", Utc(2026, 11, 19, 9))
        .CreatedAt("nov-22-sunday", Utc(2026, 11, 22, 15))
        .CreatedAt("july-4", Utc(2026, 7, 4));

    [TestCase("XXXX-12-25", "xmas-1999,xmas-2024,xmas-2025")]
    [TestCase("19XX-12-25", "xmas-1999")]
    [TestCase("199X", "xmas-1999")]
    [TestCase("2026-12-25", "")]
    [TestCase("2026-12-24", "dec-24,seattle-xmas-eve")]
    [TestCase("2026-W47", "nov-19-morning,nov-22-sunday")]
    [TestCase("2026-SU", "july-4")]
    [TestCase("XXXX-WXX-7", "nov-22-sunday")]
    [TestCase("XXXX-XX-XXTMO", "nov-19-morning,xmas-2024")]
    [TestCase("XXXX-WXX-4TMO", "nov-19-morning")]
    [TestCase("XXXX-WXX-4TAF", "dec-24")] // 2026-12-24 is a Thursday, noon
    [TestCase("2026-11-19TMO", "nov-19-morning")]
    [TestCase("2026-11-19TAF", "")]
    [TestCase("XXXX-Q4", "dec-24,dec-26,nov-19-morning,nov-22-sunday,seattle-xmas-eve,xmas-1999,xmas-2024,xmas-2025")]
    public void TimexFilters(string timex, string expected)
    {
        using var ix = Index();
        Assert.That(string.Join(",", ix.Created_(timex)), Is.EqualTo(expected));
    }

    [Test]
    public void SeattleChristmasEveIsNotChristmas()
    {
        using var ix = Index();
        Assert.That(ix.Created_("XXXX-12-25"), Does.Not.Contain("seattle-xmas-eve"));
        Assert.That(ix.Created_("2026-12-25"), Does.Not.Contain("seattle-xmas-eve"));
        Assert.That(ix.Created_("XXXX-12-24"), Does.Contain("seattle-xmas-eve"));
    }

    [Test]
    public void RangesAndMods()
    {
        using var ix = Index();
        var ctx = TestIndex.Ctx(2026, 11, 17);
        Assert.That(ix.Search(ix.Created.MakeRangeQuery("2026-11", "2026-12", true, true, ctx)),
            Is.EqualTo(new[] { "dec-24", "dec-26", "nov-19-morning", "nov-22-sunday", "seattle-xmas-eve" }));
        Assert.That(ix.Search(ix.Created.MakeRangeQuery("2026-11", "2026-12", true, false, ctx)),
            Is.EqualTo(new[] { "nov-19-morning", "nov-22-sunday" }));
        Assert.That(ix.Search(ix.Created.MakeRangeQuery(null, "2000", true, false, ctx)), Is.EqualTo(new[] { "xmas-1999" }));
        Assert.That(ix.Created_("2026-12", mod: "after"), Is.Empty);
        Assert.That(ix.Created_("2025", mod: "before"), Is.EqualTo(new[] { "xmas-1999", "xmas-2024" }));
    }
}
