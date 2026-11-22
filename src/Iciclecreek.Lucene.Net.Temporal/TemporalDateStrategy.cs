using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Strategy for a concrete date property (Created, Sent) that can be filtered with whole or partial
/// TIMEX values: <c>XXXX-12-25</c>, <c>XXXX-WXX-1TMO</c>, <c>2026-11</c>, <c>19XX-12-25</c>.
/// </summary>
/// <remarks>Physical fields: <c>{name}$ticks</c> (local wall-clock ticks), <c>{name}$sig</c> (signature tokens), <c>{name}$v</c> (stored format version).</remarks>
public sealed class TemporalDateStrategy : TemporalStrategy
{
    public TemporalDateStrategy(string fieldName, TemporalOptions? options = null)
        : base(fieldName, options)
    {
    }

    /// <summary>Numeric field holding local wall-clock ticks.</summary>
    public string TicksFieldName => FieldName + "$ticks";

    /// <summary>Fields for one date value. Components are computed in <paramref name="zone"/> (default: options' zone).</summary>
    public Field[] CreateIndexableFields(DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(value, zone ?? Options.DefaultZone).DateTime;
        return
        [
            new Int64Field(TicksFieldName, local.Ticks, Field.Store.NO),
            SignatureField.Create(SignatureFieldName, TemporalSignatures.ForDateValue(local)),
            new StoredField(VersionFieldName, FormatVersion),
        ];
    }

    protected internal override Query BuildQuery(TimexValue value)
    {
        Query q = value.Kind switch
        {
            TimexValueKind.Recurring => PatternQuery(value.Patterns),
            TimexValueKind.Anchored when value.PartialYears is { } py => All(
                PatternQuery([py.Pattern]),
                Terms(SignatureFieldName, py.Years.Select(y => TemporalSignatures.A($"Y{y:0000}", "_")))),
            TimexValueKind.Anchored => AnchoredQuery(value.Spans),
            TimexValueKind.OpenEnded => Ticks(value.Start, value.End),
            _ => MatchNothing(),
        };
        return new ConstantScoreQuery(q);
    }

    private Query PatternQuery(IEnumerable<RecurringPattern> patterns)
    {
        var tokens = new List<string>();
        foreach (var p in patterns)
        {
            IEnumerable<string> times = p.Time is null ? ["_"] : p.Time.IndexValues;
            foreach (var t in times)
                tokens.Add(TemporalSignatures.R(p.Period, p.Day, t));
        }
        if (tokens.Count == 1)
            return new TermQuery(new Term(SignatureFieldName, tokens[0]));
        return Terms(SignatureFieldName, tokens);
    }

    private Query AnchoredQuery(IReadOnlyList<AnchoredSpan> spans)
    {
        var ranges = new List<Query>();
        foreach (var span in spans)
        {
            if (span.Time is null || span.Days > 366)
            {
                ranges.Add(Ticks(span.Start.ToDateTime(TimeOnly.MinValue), span.End.ToDateTime(TimeOnly.MinValue)));
                continue;
            }
            foreach (var day in span.EachDay())
            {
                var midnight = day.ToDateTime(TimeOnly.MinValue);
                foreach (var (start, end) in span.Time.Intervals)
                    ranges.Add(Ticks(midnight.AddMinutes(start), midnight.AddMinutes(end)));
            }
        }

        if (ranges.Count == 1)
            return ranges[0];
        if (ranges.Count > BooleanQuery.MaxClauseCount)
            return Ticks(spans.Min(s => s.StartInstant), spans.Max(s => s.EndInstant));
        return Any(ranges.ToArray());
    }

    private Query Ticks(DateTime? start, DateTime? end) =>
        NumericRangeQuery.NewInt64Range(TicksFieldName, start?.Ticks, end?.Ticks, true, false);
}
