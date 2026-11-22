using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Iciclecreek.Lucene.Net.Temporal.Tests;

/// <summary>In-memory index with a "created" date field and a "when" TIMEX field.</summary>
internal sealed class TestIndex : IDisposable
{
    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    public static readonly TimeZoneInfo Seattle = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private readonly RAMDirectory _dir = new();
    private readonly IndexWriter _writer;
    private DirectoryReader? _reader;

    public TestIndex(TemporalOptions? options = null)
    {
        Created = new TemporalDateStrategy("created", options);
        When = new TimexStrategy("when", options);
        _writer = new IndexWriter(_dir, new IndexWriterConfig(LuceneVersion.LUCENE_48, new StandardAnalyzer(LuceneVersion.LUCENE_48)));
    }

    public TemporalDateStrategy Created { get; }

    public TimexStrategy When { get; }

    public static TemporalContext Ctx(int year, int month, int day, TimeZoneInfo? zone = null) =>
        new(zone ?? Utc, new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Doc with "when" references anchored at <paramref name="sent"/>.</summary>
    public TestIndex Mentions(string id, TemporalContext sent, params TimexReference[] refs)
    {
        var doc = new Document { new StringField("id", id, Field.Store.YES) };
        doc.AddTimexField(When, refs, sent);
        _writer.AddDocument(doc);
        return this;
    }

    public TestIndex CreatedAt(string id, DateTimeOffset created, TimeZoneInfo? zone = null)
    {
        var doc = new Document { new StringField("id", id, Field.Store.YES) };
        doc.AddTemporalDateField(Created, created, zone ?? Utc);
        _writer.AddDocument(doc);
        return this;
    }

    public string[] Search(Query query)
    {
        _reader?.Dispose();
        _reader = DirectoryReader.Open(_writer, applyAllDeletes: true);
        var searcher = new IndexSearcher(_reader);
        var hits = searcher.Search(query, 1000);
        return hits.ScoreDocs.Select(sd => searcher.Doc(sd.Doc).Get("id")).Order().ToArray();
    }

    /// <summary>Ids of "when" docs matching a TIMEX query (query "now" = 2026-11-17).</summary>
    public string[] When_(string timex, string? mod = null) => Search(When.MakeQuery(timex, Ctx(2026, 11, 17), mod));

    public string[] Created_(string timex, string? mod = null) => Search(Created.MakeQuery(timex, Ctx(2026, 11, 17), mod));

    public void Dispose()
    {
        _reader?.Dispose();
        _writer.Dispose();
        _dir.Dispose();
    }
}
