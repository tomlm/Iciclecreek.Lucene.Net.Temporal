using System.Text;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// A temporal query on one strategy's field. Holds the normalized TIMEX value and expands itself
/// into term/numeric/regexp queries on rewrite, like <c>MultiTermQuery</c> and the spatial queries.
/// </summary>
public sealed class TimexQuery : Query
{
    private readonly string _display;

    internal TimexQuery(TemporalStrategy strategy, TimexValue value, string display)
    {
        Strategy = strategy;
        Value = value;
        _display = display;
    }

    public TemporalStrategy Strategy { get; }

    public TimexValue Value { get; }

    public string Field => Strategy.FieldName;

    public override Query Rewrite(IndexReader reader)
    {
        var q = Strategy.BuildQuery(Value);
        q.Boost = Boost;
        return q;
    }

    public override string ToString(string field)
    {
        var sb = new StringBuilder();
        if (field != Field)
            sb.Append(Field).Append(':');
        sb.Append(_display);
        sb.Append(ToStringUtils.Boost(Boost));
        return sb.ToString();
    }

    public override bool Equals(object? obj) =>
        obj is TimexQuery other
        && base.Equals(other)
        && other.Field == Field
        && other.Strategy.GetType() == Strategy.GetType()
        && other.Value.Key == Value.Key;

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), Field, Strategy.GetType(), Value.Key);
}
