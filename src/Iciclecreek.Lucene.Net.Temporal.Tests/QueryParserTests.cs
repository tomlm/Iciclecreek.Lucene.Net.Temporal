using Lucene.Net.Analysis.Standard;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Util;

namespace Iciclecreek.Lucene.Net.Temporal.Tests;

public class QueryParserTests
{
    private static TemporalQueryParser Parser(TestIndex ix) =>
        new(LuceneVersion.LUCENE_48, "body", new StandardAnalyzer(LuceneVersion.LUCENE_48),
            [ix.Created, ix.When], TestIndex.Ctx(2026, 11, 17));

    [Test]
    public void RoutesTemporalFields()
    {
        using var ix = new TestIndex()
            .CreatedAt("xmas", new DateTimeOffset(2025, 12, 25, 12, 0, 0, TimeSpan.Zero))
            .CreatedAt("nov", new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero))
            .Mentions("last-week", TestIndex.Ctx(2026, 1, 1), "XXXX-XX-W05")
            .Mentions("tue-9am", TestIndex.Ctx(2026, 1, 1), "XXXX-WXX-2T09");
        var parser = Parser(ix);

        Assert.That(ix.Search(parser.Parse("created:XXXX-12-25")), Is.EqualTo(new[] { "xmas" }));
        Assert.That(ix.Search(parser.Parse("created:[2026-11 TO 2026-12]")), Is.EqualTo(new[] { "nov" }));
        Assert.That(ix.Search(parser.Parse("created:[2026-11 TO *]")), Is.EqualTo(new[] { "nov" }));
        Assert.That(ix.Search(parser.Parse("when:2026-11-W05")), Is.EqualTo(new[] { "last-week", "tue-9am" })); // includes Tue Nov 24
        Assert.That(ix.Search(parser.Parse("when:\"2026-11-17T09:30\"")), Is.EqualTo(new[] { "tue-9am" }));
        Assert.That(ix.Search(parser.Parse("when:\"(2026-11-23,2026-11-26,P3D)\"")), Is.EqualTo(new[] { "last-week", "tue-9am" }));
        Assert.That(ix.Search(parser.Parse("created:XXXX-12-25 OR when:2026-11-W05")), Is.EqualTo(new[] { "last-week", "tue-9am", "xmas" }));
    }

    [Test]
    public void ToStringRoundTrips()
    {
        using var ix = new TestIndex();
        var q = Parser(ix).Parse("created:XXXX-12-25 AND when:[2026-11 TO 2026-12}");
        Assert.That(q.ToString(), Is.EqualTo("+created:XXXX-12-25 +when:[2026-11 TO 2026-12}"));
    }

    [Test]
    public void InvalidTimexIsAParseException()
    {
        using var ix = new TestIndex();
        Assert.Throws<ParseException>(new Action(() => Parser(ix).Parse("created:2026-13")));
    }
}
