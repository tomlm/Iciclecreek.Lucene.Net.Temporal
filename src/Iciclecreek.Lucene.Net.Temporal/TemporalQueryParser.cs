using Lucene.Net.Analysis;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Classic query parser that routes temporal fields to their strategies:
/// <c>created:XXXX-12-25</c>, <c>when:2026-11-W05</c>, <c>created:[2026-11 TO 2026-12]</c>.
/// TIMEX values containing ':' (times), '(' or ',' (ranges) must be quoted.
/// </summary>
public class TemporalQueryParser : QueryParser
{
    private readonly Dictionary<string, TemporalStrategy> _strategies;

    public TemporalQueryParser(LuceneVersion matchVersion, string defaultField, Analyzer analyzer,
        IEnumerable<TemporalStrategy> strategies, TemporalContext? context = null)
        : base(matchVersion, defaultField, analyzer)
    {
        _strategies = strategies.ToDictionary(s => s.FieldName, StringComparer.Ordinal);
        Context = context;
    }

    /// <summary>Zone and "now" for relative query values. Null = current time in each strategy's default zone.</summary>
    public TemporalContext? Context { get; set; }

    protected override Query GetFieldQuery(string field, string queryText, bool quoted) =>
        _strategies.TryGetValue(field, out var s) ? Temporal(() => s.MakeQuery(queryText, Context)) : base.GetFieldQuery(field, queryText, quoted);

    protected override Query GetFieldQuery(string field, string queryText, int slop) =>
        _strategies.TryGetValue(field, out var s) ? Temporal(() => s.MakeQuery(queryText, Context)) : base.GetFieldQuery(field, queryText, slop);

    protected override Query GetRangeQuery(string field, string part1, string part2, bool startInclusive, bool endInclusive) =>
        _strategies.TryGetValue(field, out var s)
            ? Temporal(() => s.MakeRangeQuery(part1, part2, startInclusive, endInclusive, Context))
            : base.GetRangeQuery(field, part1, part2, startInclusive, endInclusive);

    private static Query Temporal(Func<Query> make)
    {
        try
        {
            return make();
        }
        catch (FormatException ex)
        {
            throw new ParseException(ex.Message);
        }
    }
}
