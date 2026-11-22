using Lucene.Net.Documents;

namespace Iciclecreek.Lucene.Net.Temporal.Tests;

public class TimexFieldTests
{
    private static readonly TemporalContext Sent = TestIndex.Ctx(2026, 1, 5);

    private static TestIndex Index(TemporalOptions? options = null) => new TestIndex(options)
        .Mentions("first-of-month", Sent, "XXXX-XX-01")
        .Mentions("thursdays", Sent, "XXXX-WXX-4")
        .Mentions("first-thursday", Sent, "XXXX-XX-WXX-4-#1")
        .Mentions("last-friday", Sent, "XXXX-XX-WXX-5-#5")
        .Mentions("last-week", Sent, "XXXX-XX-W05")
        .Mentions("mondays-in-may", Sent, "XXXX-05-WXX-1")
        .Mentions("tuesday-mornings", Sent, "XXXX-WXX-2TMO")
        .Mentions("multi", Sent, "XXXX-XX-01", "XXXX-WXX-2")
        .Mentions("nov-26", TestIndex.Ctx(2026, 11, 17), new TimexReference("2026-11-26", Type: "date"))
        .Mentions("nov-19-10am", Sent, "2026-11-19T10")
        .Mentions("may-20", Sent, "2026-05-20");

    [TestCase("2026-05-01", "first-of-month,multi")]
    [TestCase("2026-06-01", "first-of-month,multi")]
    [TestCase("2026-05-02", "")]
    public void DayOfMonth(string date, string expected) => AssertWhen(Index(), date, expected, only: ["first-of-month", "multi"]);

    [Test]
    public void NthAndLastWeekday()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-11-05"), Does.Contain("first-thursday").And.Contain("thursdays"));
        Assert.That(ix.When_("2026-11-12"), Does.Not.Contain("first-thursday").And.Contain("thursdays"));
        Assert.That(ix.When_("2026-11-27"), Does.Contain("last-friday").And.Contain("last-week"));
        Assert.That(ix.When_("2026-11-20"), Does.Not.Contain("last-friday"));
    }

    [Test]
    public void LastWeekOfEveryMonth()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-11-W05"), Does.Contain("last-week"), "last week of this month");
        Assert.That(ix.When_("2026-11-24"), Does.Contain("last-week"));
        Assert.That(ix.When_("2026-11-23"), Does.Not.Contain("last-week"));
        Assert.That(ix.When_("2026-02-22"), Does.Contain("last-week"));
        Assert.That(ix.When_("2026-02-21"), Does.Not.Contain("last-week"));
    }

    [Test]
    public void AnchoredReferenceMatchesOnlyItsDate()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-11-26"), Does.Contain("nov-26"));
        Assert.That(ix.When_("2026-11-25"), Does.Not.Contain("nov-26"));
        Assert.That(ix.When_("2026-11"), Does.Contain("nov-26"));
        Assert.That(ix.When_("2026-W48"), Does.Contain("nov-26"));
    }

    [Test]
    public void MultipleReferencesDoNotCombine()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-04-15"), Does.Not.Contain("multi"), "Wednesday the 15th");
        Assert.That(ix.When_("2026-04-01"), Does.Contain("multi"));
        Assert.That(ix.When_("2026-04-14"), Does.Contain("multi"));
    }

    [Test]
    public void CompoundPattern()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-05-04"), Does.Contain("mondays-in-may"));
        Assert.That(ix.When_("2026-06-01"), Does.Not.Contain("mondays-in-may"));
        Assert.That(ix.When_("2026-05-05"), Does.Not.Contain("mondays-in-may"));
    }

    [Test]
    public void TimeOfDay()
    {
        using var ix = Index();
        Assert.That(ix.When_("2026-11-17"), Does.Contain("tuesday-mornings"));
        Assert.That(ix.When_("2026-11-17T09:00"), Does.Contain("tuesday-mornings"));
        Assert.That(ix.When_("2026-11-17T15:00"), Does.Not.Contain("tuesday-mornings"));
        Assert.That(ix.When_("2026-11-17TAF"), Does.Not.Contain("tuesday-mornings"));

        Assert.That(ix.When_("2026-11-19"), Does.Contain("nov-19-10am"));
        Assert.That(ix.When_("2026-11-19TMO"), Does.Contain("nov-19-10am"));
        Assert.That(ix.When_("2026-11-19T10:30"), Does.Contain("nov-19-10am"));
        Assert.That(ix.When_("2026-11-19T15"), Does.Not.Contain("nov-19-10am"));
    }

    [Test]
    public void Ranges()
    {
        using var ix = Index();
        var may = ix.When_("2026-05");
        Assert.That(may, Does.Contain("first-of-month").And.Contain("may-20").And.Contain("mondays-in-may").And.Not.Contain("nov-26"));

        var multiYear = ix.Search(ix.When.MakeRangeQuery("2020", "2026", true, true, TestIndex.Ctx(2026, 11, 17)));
        Assert.That(multiYear, Does.Contain("nov-26").And.Contain("thursdays"));

        var before = ix.Search(ix.When.MakeRangeQuery("2020", "2025", true, true, TestIndex.Ctx(2026, 11, 17)));
        Assert.That(before, Does.Not.Contain("nov-26").And.Not.Contain("thursdays"), "recurrences anchored in 2026 don't apply to 2020-2025");

        var after = ix.When_("2026-06", mod: "after");
        Assert.That(after, Does.Contain("nov-26").And.Not.Contain("may-20"));
    }

    [Test]
    public void RecurrenceValidity()
    {
        using var ix = new TestIndex()
            .Mentions("future-recurrence", TestIndex.Ctx(2027, 1, 10), "XXXX-XX-01")
            .Mentions("past-christmas", TestIndex.Ctx(2026, 3, 1), new TimexReference("2025-12-25", Type: "date"));
        Assert.That(ix.When_("2026-05-01"), Is.Empty);
        Assert.That(ix.When_("2025-12-25"), Is.EqualTo(new[] { "past-christmas" }));
    }

    [Test]
    public void RecurrenceHorizon()
    {
        using var ix = new TestIndex(new TemporalOptions { RecurrenceHorizon = TimeSpan.FromDays(365) })
            .Mentions("old-thursdays", TestIndex.Ctx(2019, 11, 1), "XXXX-WXX-4");
        Assert.That(ix.When_("2019-11-21"), Is.EqualTo(new[] { "old-thursdays" }));
        Assert.That(ix.When_("2026-11-19"), Is.Empty);
    }

    [Test]
    public void PatternQueries()
    {
        using var ix = Index()
            .Mentions("fifteenth", Sent, "XXXX-XX-15")
            .Mentions("every-day", Sent, "P1D")
            .Mentions("christmas-2026", Sent, new TimexReference("XXXX-12-25", Type: "date"))
            .Mentions("every-christmas", Sent, "XXXX-12-25")
            .Mentions("fridays", Sent, "XXXX-WXX-5");

        var lastWeek = ix.When_("XXXX-XX-W05");
        Assert.That(lastWeek, Does.Contain("last-week").And.Contain("last-friday").And.Contain("every-day"));
        Assert.That(lastWeek, Does.Not.Contain("fifteenth").And.Not.Contain("thursdays"));

        var christmas = ix.When_("XXXX-12-25");
        Assert.That(christmas, Does.Contain("christmas-2026").And.Contain("every-christmas").And.Contain("every-day"));
        Assert.That(christmas, Does.Not.Contain("fridays").And.Not.Contain("first-of-month"));

        var thursdays = ix.When_("XXXX-WXX-4");
        Assert.That(thursdays, Does.Contain("thursdays").And.Contain("first-thursday").And.Contain("nov-26"));
        Assert.That(thursdays, Does.Not.Contain("fridays"));

        var mornings = ix.When_("TMO");
        Assert.That(mornings, Does.Contain("tuesday-mornings").And.Contain("nov-19-10am").And.Contain("thursdays"));

        Assert.That(ix.When_("XXXX"), Has.Length.EqualTo(16));
    }

    [Test]
    public void RecognizersStyleValues()
    {
        var ctx = TestIndex.Ctx(2026, 11, 17);
        using var ix = new TestIndex()
            .Mentions("nineties", ctx, new TimexReference("(XX90-01-01,XX00-01-01,P10Y)", Type: "daterange"))
            .Mentions("q2", ctx, new TimexReference("(2026-04-01,2026-07-01,P3M)", Type: "daterange"))
            .Mentions("weekend", ctx, new TimexReference("2026-W47-WE", Type: "daterange"))
            .Mentions("since-june", ctx, new TimexReference("XXXX-06", Mod: "since", Type: "daterange"));

        Assert.That(ix.When_("1995-06-01"), Is.EqualTo(new[] { "nineties" }));
        Assert.That(ix.When_("2026-05-15"), Is.EqualTo(new[] { "q2" }));
        Assert.That(ix.When_("2026-11-22"), Is.EqualTo(new[] { "weekend" }));
        Assert.That(ix.When_("2026-11-20"), Is.Empty, "open-ended references are stored but not indexed in v1");
    }

    [Test]
    public void AnchorMustMatchAcrossCalls()
    {
        var when = new TimexStrategy("when");
        var doc = new Document();
        doc.AddTimexField(when, "XXXX-WXX-4", TestIndex.Ctx(2026, 1, 1));
        doc.AddTimexField(when, "XXXX-WXX-5", TestIndex.Ctx(2026, 1, 1));
        Assert.Throws<InvalidOperationException>(new Action(() => doc.AddTimexField(when, "XXXX-WXX-1", TestIndex.Ctx(2026, 2, 1))));
        Assert.That(doc.GetFields("when$anchor"), Has.Length.EqualTo(1));
        Assert.That(doc.GetValues("when"), Is.EqualTo(new[] { "XXXX-WXX-4", "XXXX-WXX-5" }));
    }

    [Test]
    public void InvalidReferencesCanBeIgnored()
    {
        var lenient = new TimexStrategy("when", new TemporalOptions { IgnoreInvalidReferences = true });
        Assert.That(lenient.CreateIndexableFields(["garbage", "XXXX-WXX-4"], Sent).Select(f => f.Name), Does.Contain("when$sig"));
        Assert.Throws<FormatException>(new Action(() => new TimexStrategy("when").CreateIndexableFields(["garbage"], Sent)));
    }

    [Test]
    public void TimexQueryIdentity()
    {
        var ctx = TestIndex.Ctx(2026, 11, 17);
        var when = new TimexStrategy("when");
        var a = when.MakeQuery("XXXX-XX-W05", ctx);
        var b = when.MakeQuery("xxxx-xx-w05", ctx);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        Assert.That(a.ToString(), Is.EqualTo("when:XXXX-XX-W05"));
        Assert.That(when.MakeQuery("XXXX-XX-W04", ctx), Is.Not.EqualTo(a));
    }

    private static void AssertWhen(TestIndex ix, string date, string expected, string[] only)
    {
        using (ix)
        {
            var hits = ix.When_(date).Intersect(only).Order();
            Assert.That(string.Join(",", hits), Is.EqualTo(expected));
        }
    }
}
