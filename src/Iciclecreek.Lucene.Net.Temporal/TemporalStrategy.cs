using Lucene.Net.Index;
using Lucene.Net.Queries;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Owns one logical temporal field: its physical field layout, indexable-field creation and query
/// construction. Modeled on Lucene.Net.Spatial's <c>SpatialStrategy</c>.
/// </summary>
public abstract class TemporalStrategy
{
    protected TemporalStrategy(string fieldName, TemporalOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Field name is required.", nameof(fieldName));
        FieldName = fieldName;
        Options = options ?? TemporalOptions.Default;
    }

    public string FieldName { get; }

    public TemporalOptions Options { get; }

    /// <summary>Field holding signature tokens.</summary>
    public string SignatureFieldName => FieldName + "$sig";

    /// <summary>Stored field holding the token-format version.</summary>
    public string VersionFieldName => FieldName + "$v";

    /// <summary>Token-format version written by this library.</summary>
    public static string FormatVersion => TemporalSignatures.FormatVersion;

    /// <summary>Query for a TIMEX value, e.g. <c>XXXX-12-25</c>, <c>2026-11-W05</c>, <c>2026-SU</c>.
    /// Unspecified-year values are patterns; relative values resolve against <paramref name="context"/>.</summary>
    public TimexQuery MakeQuery(string timex, TemporalContext? context = null, string? mod = null)
    {
        var value = TimexValue.Parse(timex, context ?? DefaultContext(), mod);
        return new TimexQuery(this, value, mod is null ? timex.Trim() : $"{timex.Trim()}[{mod}]");
    }

    /// <summary>Range between TIMEX cells: <c>[2026-11 TO 2026-12]</c> = start of Nov to end of Dec. Null = open.</summary>
    public TimexQuery MakeRangeQuery(string? lower, string? upper, bool includeLower, bool includeUpper, TemporalContext? context = null)
    {
        var value = TimexValue.Range(lower, upper, includeLower, includeUpper, context ?? DefaultContext());
        return new TimexQuery(this, value, value.Source);
    }

    public Filter MakeFilter(string timex, TemporalContext? context = null, string? mod = null) =>
        new QueryWrapperFilter(MakeQuery(timex, context, mod));

    /// <summary>Builds the concrete Lucene query; called by <see cref="TimexQuery.Rewrite"/>.</summary>
    protected internal abstract Query BuildQuery(TimexValue value);

    protected TemporalContext DefaultContext() => new(Options.DefaultZone, DateTimeOffset.Now);

    internal static Query MatchNothing() => new BooleanQuery();

    internal static Query Terms(string field, IEnumerable<string> terms)
    {
        var bytes = terms.Distinct(StringComparer.Ordinal).Select(t => new BytesRef(t)).ToArray();
        return bytes.Length == 0 ? MatchNothing() : new ConstantScoreQuery(new TermsFilter(field, bytes));
    }

    internal static Query Any(params Query[] queries)
    {
        var bq = new BooleanQuery();
        foreach (var q in queries)
            bq.Add(q, Occur.SHOULD);
        return bq;
    }

    internal static Query All(params Query[] queries)
    {
        var bq = new BooleanQuery();
        foreach (var q in queries)
            bq.Add(q, Occur.MUST);
        return bq;
    }
}
