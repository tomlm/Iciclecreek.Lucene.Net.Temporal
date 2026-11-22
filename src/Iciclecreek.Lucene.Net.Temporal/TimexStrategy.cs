using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Strategy for a field of TIMEX references (typically extracted from a document's text):
/// "every Thursday" (<c>XXXX-WXX-4</c>), "last week of every month" (<c>XXXX-XX-W05</c>),
/// "May 1-10" (<c>(2026-05-01,2026-05-10,P9D)</c>).
/// </summary>
/// <remarks>
/// Physical fields: <c>{name}$sig</c> (signature tokens), <c>{name}$anchor</c> (local anchor ticks, one per doc),
/// <c>{name}</c> (stored raw references), <c>{name}$v</c> (stored format version).
/// </remarks>
public sealed class TimexStrategy : TemporalStrategy
{
    private const int MaxEnumeratedDays = 4 * 366;
    private const string High = "￿";

    public TimexStrategy(string fieldName, TemporalOptions? options = null)
        : base(fieldName, options)
    {
    }

    /// <summary>Numeric field holding the references' anchor as local wall-clock ticks.</summary>
    public string AnchorFieldName => FieldName + "$anchor";

    /// <summary>
    /// Fields for all of a document's references in this field. They share one context: its anchor is the
    /// document's sent/created time, used to resolve relative values and to bound recurrences.
    /// </summary>
    public Field[] CreateIndexableFields(IEnumerable<TimexReference> references, TemporalContext context) =>
        CreateIndexableFields(references, context, includeAnchor: true);

    public Field[] CreateIndexableFields(TimexReference reference, TemporalContext context) =>
        CreateIndexableFields([reference], context);

    internal Field[] CreateIndexableFields(IEnumerable<TimexReference> references, TemporalContext context, bool includeAnchor)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(context);

        var tokens = new List<string>();
        var fields = new List<Field>();
        foreach (var r in references)
        {
            TimexValue value;
            try
            {
                value = TimexValue.Parse(r.Timex, context, r.Mod, r.Type);
            }
            catch (FormatException) when (Options.IgnoreInvalidReferences)
            {
                continue;
            }
            tokens.AddRange(TemporalSignatures.ForReference(value, Options.MaxSatisfiesSpanDays));
            fields.Add(new StoredField(FieldName, r.ToStoredString()));
        }

        if (tokens.Count > 0)
            fields.Add(SignatureField.Create(SignatureFieldName, tokens));
        if (includeAnchor)
        {
            fields.Add(new Int64Field(AnchorFieldName, context.AnchorLocal.Ticks, Field.Store.YES));
            fields.Add(new StoredField(VersionFieldName, FormatVersion));
        }
        return fields.ToArray();
    }

    protected internal override Query BuildQuery(TimexValue value)
    {
        Query q = value.Kind switch
        {
            TimexValueKind.Recurring => PatternQuery(value.Patterns),
            TimexValueKind.Anchored => OverlapQuery(value),
            TimexValueKind.OpenEnded => RangeOverlap(value.Start, value.End),
            _ => MatchNothing(),
        };
        return new ConstantScoreQuery(q);
    }

    /// <summary>Pattern query: references comparable to the pattern on every slot.</summary>
    private Query PatternQuery(IReadOnlyList<RecurringPattern> patterns)
    {
        if (patterns.Any(p => p.IsUnconstrained))
            return TermRangeQuery.NewStringRange(SignatureFieldName, null, null, true, true);

        var queries = patterns
            .Select(p => (Query)new RegexpQuery(new Term(SignatureFieldName, PatternLattice.BuildRegex(p))))
            .ToArray();
        return queries.Length == 1 ? queries[0] : Any(queries);
    }

    /// <summary>Anchored query: references covering any day in the query spans.</summary>
    private Query OverlapQuery(TimexValue value)
    {
        int totalDays = value.Spans.Sum(s => s.Days);
        if (totalDays > MaxEnumeratedDays && value.Spans.Count <= 64)
        {
            var perSpan = value.Spans.Select(s => RangeOverlap(s.StartInstant, s.EndInstant)).ToArray();
            return perSpan.Length == 1 ? perSpan[0] : Any(perSpan);
        }
        if (totalDays > 40_000)
            throw new NotSupportedException($"TIMEX '{value.Source}' expands to too many days ({totalDays}).");

        var rTokens = new HashSet<string>(StringComparer.Ordinal);
        var aTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in value.Spans)
            foreach (var day in span.EachDay())
                TemporalSignatures.AddQueryDay(day, span.Time, rTokens, aTokens);

        return Any(
            WithValidity(Terms(SignatureFieldName, rTokens), value.Start, value.End),
            Terms(SignatureFieldName, aTokens));
    }

    /// <summary>Overlap for long or open spans: term ranges over each cell granularity plus any recurrence.</summary>
    private Query RangeOverlap(DateTime? start, DateTime? end)
    {
        DateOnly? first = start is null ? null : DateOnly.FromDateTime(start.Value);
        DateOnly? last = end is null ? null : DateOnly.FromDateTime(end.Value.AddTicks(-1));

        var cells = new List<Query>();
        foreach (var (prefix, cell) in new (string, Func<DateOnly, string>)[]
        {
            ("Y", TemporalCalendar.YearCell),
            ("Q", TemporalCalendar.QuarterCell),
            ("M", TemporalCalendar.MonthCell),
            ("W", TemporalCalendar.WeekCell),
            ("D", TemporalCalendar.DayCell),
        })
        {
            string lower = first is null ? $"A:{prefix}" : $"A:{cell(first.Value)}";
            string upper = last is null ? $"A:{prefix}{High}" : $"A:{cell(last.Value)}|{High}";
            cells.Add(TermRangeQuery.NewStringRange(SignatureFieldName, lower, upper, true, true));
        }

        var recurring = new PrefixQuery(new Term(SignatureFieldName, "R:"));
        return Any([WithValidity(recurring, start, end), .. cells]);
    }

    /// <summary>Recurring references only apply from their anchor on (and within the horizon, if set).</summary>
    private Query WithValidity(Query recurring, DateTime? queryStart, DateTime? queryEnd)
    {
        long? min = null;
        if (Options.RecurrenceHorizon is { } horizon && queryStart is { } qs)
            min = Math.Max(DateTime.MinValue.Ticks, (qs - horizon).Ticks);
        long? max = queryEnd?.Ticks;
        if (min is null && max is null)
            return recurring;
        return All(recurring, NumericRangeQuery.NewInt64Range(AnchorFieldName, min, max, true, false));
    }
}
