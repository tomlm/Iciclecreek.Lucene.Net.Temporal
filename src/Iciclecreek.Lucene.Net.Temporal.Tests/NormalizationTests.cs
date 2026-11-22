namespace Iciclecreek.Lucene.Net.Temporal.Tests;

public class NormalizationTests
{
    private static readonly TemporalContext Ctx = TestIndex.Ctx(2026, 11, 17);

    private static RecurringPattern Pattern(string timex)
    {
        var v = TimexValue.Parse(timex, Ctx);
        Assert.That(v.Kind, Is.EqualTo(TimexValueKind.Recurring), timex);
        return v.Patterns.Single();
    }

    private static (DateOnly Start, DateOnly End) Span(string timex, string? type = null)
    {
        var v = TimexValue.Parse(timex, Ctx, type: type);
        Assert.That(v.Kind, Is.EqualTo(TimexValueKind.Anchored), timex);
        return (v.Spans.First().Start, v.Spans.Last().End);
    }

    [TestCase("XXXX-12-25", "M12|D25|_")]
    [TestCase("XXXX-XX-01", "_|D01|_")]
    [TestCase("XXXX-WXX-4", "_|DOW4|_")]
    [TestCase("xxxx-wxx-2tmo", "_|DOW2|MO")]
    [TestCase("XXXX-11-WXX-4-#1", "M11|NDOW1-4|_")]
    [TestCase("XXXX-XX-WXX-5-#5", "_|LDOW5|_")]
    [TestCase("XXXX-XX-WXX-5-#-1", "_|LDOW5|_")]
    [TestCase("XXXX-XX-W05", "_|LWOM|_")]
    [TestCase("XXXX-XX-W02", "_|WOM2|_")]
    [TestCase("XXXX-05-WXX-1", "M05|DOW1|_")]
    [TestCase("XXXX-SU", "SSU|_|_")]
    [TestCase("SU", "SSU|_|_")]
    [TestCase("XXXX-Q2", "Q2|_|_")]
    [TestCase("XXXX-W47", "W47|_|_")]
    [TestCase("XXXX-WXX-WE", "_|WE|_")]
    [TestCase("P1WE", "_|WE|_")]
    [TestCase("TMO", "_|_|MO")]
    [TestCase("T10", "_|_|10")]
    public void RecurringPatterns(string timex, string key) =>
        Assert.That(Pattern(timex).Key, Is.EqualTo(key));

    [TestCase("2026-11-19", "2026-11-19", "2026-11-20")]
    [TestCase("2026-11", "2026-11-01", "2026-12-01")]
    [TestCase("2026-Q2", "2026-04-01", "2026-07-01")]
    [TestCase("2026-W47", "2026-11-16", "2026-11-23")]
    [TestCase("2026-W53", "2026-12-28", "2027-01-04")]
    [TestCase("2026-11-W05", "2026-11-24", "2026-12-01")]
    [TestCase("2026-02-W05", "2026-02-22", "2026-03-01")]
    [TestCase("2026-SU", "2026-06-01", "2026-09-01")]
    [TestCase("2026-WI", "2026-12-01", "2027-03-01")]
    [TestCase("2026-W47-WE", "2026-11-21", "2026-11-23")]
    [TestCase("199X", "1990-01-01", "2000-01-01")]
    [TestCase("(2026-05-01,2026-05-10,P9D)", "2026-05-01", "2026-05-10")]
    [TestCase("(2026-04-01,2026-07-01,P3M)", "2026-04-01", "2026-07-01")]
    [TestCase("(XX90-01-01,XX00-01-01,P10Y)", "1990-01-01", "2000-01-01")]
    [TestCase("PRESENT_REF", "2026-11-17", "2026-11-18")]
    public void AnchoredSpans(string timex, string start, string end)
    {
        var (s, e) = Span(timex);
        Assert.That((s, e), Is.EqualTo((DateOnly.Parse(start), DateOnly.Parse(end))));
    }

    [Test]
    public void PartialYearWithPatternEnumeratesYears()
    {
        var v = TimexValue.Parse("19XX-12-25", Ctx);
        Assert.That(v.Spans, Has.Count.EqualTo(100));
        Assert.That(v.PartialYears, Is.Not.Null);
    }

    [TestCase("XXXX-12-25", "date", "2026-12-25")]      // Recognizers "Christmas" from 2026-11-17
    [TestCase("XXXX-WXX-4", "date", "2026-11-19")]      // "Thursday"
    [TestCase("XXXX-05", "daterange", "2027-05-01")]    // "May": nearer in the future
    [TestCase("T10", "time", "2026-11-17")]
    public void UnspecifiedYearWithNonSetTypeResolvesNearest(string timex, string type, string start) =>
        Assert.That(Span(timex, type).Start, Is.EqualTo(DateOnly.Parse(start)));

    [Test]
    public void RecurringRanges()
    {
        var mayDays = TimexValue.Parse("(XXXX-05-01,XXXX-05-10,P9D)", Ctx);
        Assert.That(mayDays.Patterns.Select(p => p.Key), Is.EqualTo(Enumerable.Range(1, 9).Select(d => $"M05|D{d:00}|_")));

        var weekdays = TimexValue.Parse("(XXXX-WXX-1,XXXX-WXX-5,P4D)", Ctx);
        Assert.That(weekdays.Patterns.Select(p => p.Day), Is.EqualTo(new[] { "DOW1", "DOW2", "DOW3", "DOW4" }));

        var hours = TimexValue.Parse("(T09,T12,PT3H)", Ctx);
        Assert.That(hours.Patterns.Single().Time!.IndexValues, Is.EqualTo(new[] { "H09", "H10", "H11" }));
    }

    [Test]
    public void Mods()
    {
        var after = TimexValue.Parse("2026-06", Ctx, mod: "after");
        Assert.That(after.Kind, Is.EqualTo(TimexValueKind.OpenEnded));
        Assert.That(after.Start, Is.EqualTo(new DateTime(2026, 7, 1)));
        Assert.That(after.End, Is.Null);

        var before = TimexValue.Parse("2026-06", Ctx, mod: "before");
        Assert.That(before.End, Is.EqualTo(new DateTime(2026, 6, 1)));

        var approx = TimexValue.Parse("2026-06", Ctx, mod: "approx");
        Assert.That(approx.Kind, Is.EqualTo(TimexValueKind.Anchored));
    }

    [TestCase("P1M")]
    [TestCase("P3M")]
    public void BareDurationsAreNotIndexable(string timex) =>
        Assert.That(TimexValue.Parse(timex, Ctx).Kind, Is.EqualTo(TimexValueKind.None));

    [TestCase("garbage")]
    [TestCase("2026-13")]
    [TestCase("XXXX-XX-W09")]
    [TestCase("2026-02-30")]
    public void InvalidValuesThrow(string timex) =>
        Assert.Throws<FormatException>(new Action(() => TimexValue.Parse(timex, Ctx)));

    [Test]
    public void IsoWeekYearBoundary()
    {
        Assert.That(TemporalCalendar.WeekCell(new DateOnly(2026, 12, 31)), Is.EqualTo("W2026-53"));
        Assert.That(TemporalCalendar.WeekCell(new DateOnly(2027, 1, 1)), Is.EqualTo("W2026-53"));
    }

    [Test]
    public void DayValuesForLastFridayOfNovember2026()
    {
        var values = TemporalCalendar.DayValues(new DateOnly(2026, 11, 27)).ToArray();
        Assert.That(values, Does.Contain("LDOW5").And.Contain("LWOM").And.Contain("NDOW4-5").And.Not.Contain("LASTDAY"));
    }

    [Test]
    public void TilingUsesCoarsestCells()
    {
        var span = new AnchoredSpan(new DateOnly(2026, 1, 1), new DateOnly(2027, 2, 3), null);
        Assert.That(TemporalSignatures.Tile(span), Is.EqualTo(new[] { "Y2026", "M2027-01", "D2027-02-01", "D2027-02-02" }));
    }
}
