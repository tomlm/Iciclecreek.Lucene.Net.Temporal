using System.Text;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Pattern-vs-pattern matching. A reference matches a pattern query when, on every slot, its value is
/// "_", equal to the query value, or related to it by implication in either direction
/// (e.g. NDOW1-4 ⇒ DOW4, M05 ⇒ Q2, LDOW5 ⇒ LWOM). Values that merely co-occur sometimes
/// (D25 vs DOW5) don't match.
/// </summary>
internal static class PatternLattice
{
    /// <summary>Allowed reference values for a query period value, or null for "any".</summary>
    public static IEnumerable<string>? AllowedPeriods(string q)
    {
        if (q == "_") return null;
        var list = new List<string> { "_", q };
        switch (q[0])
        {
            case 'M':
                int m = int.Parse(q[1..]);
                list.Add($"Q{TemporalCalendar.Quarter(m)}");
                list.Add($"S{TemporalCalendar.Season(m)}");
                break;
            case 'Q':
                int quarter = q[1] - '0';
                for (int i = 1; i <= 3; i++) list.Add($"M{(quarter - 1) * 3 + i:00}");
                break;
            case 'S':
                foreach (var sm in TemporalCalendar.SeasonMonths(q[1..])) list.Add($"M{sm:00}");
                break;
        }
        return list;
    }

    /// <summary>Allowed reference values for a query day value, or null for "any".</summary>
    public static IEnumerable<string>? AllowedDays(string q)
    {
        if (q == "_") return null;
        var list = new List<string> { "_", q };

        if (q.StartsWith("NDOW"))
        {
            var parts = q[4..].Split('-');
            int n = int.Parse(parts[0]), k = int.Parse(parts[1]);
            list.Add($"DOW{k}");
            list.Add($"WOM{n}");
            if (k >= 6) list.Add("WE");
        }
        else if (q.StartsWith("LDOW"))
        {
            int k = int.Parse(q[4..]);
            list.Add($"DOW{k}");
            list.Add("LWOM");
            if (k >= 6) list.Add("WE");
        }
        else if (q.StartsWith("DOW"))
        {
            int k = int.Parse(q[3..]);
            for (int n = 1; n <= 4; n++) list.Add($"NDOW{n}-{k}");
            list.Add($"LDOW{k}");
            if (k >= 6) list.Add("WE");
        }
        else if (q.StartsWith("WOM"))
        {
            int n = int.Parse(q[3..]);
            for (int k = 1; k <= 7; k++) list.Add($"NDOW{n}-{k}");
            for (int d = (n - 1) * 7 + 1; d <= n * 7; d++) list.Add($"D{d:00}");
        }
        else if (q == "LWOM")
        {
            for (int k = 1; k <= 7; k++) list.Add($"LDOW{k}");
            list.Add("LASTDAY");
            list.AddRange(["D29", "D30", "D31"]);
        }
        else if (q == "LASTDAY")
        {
            list.Add("LWOM");
            list.Add("D31");
        }
        else if (q == "WE")
        {
            foreach (int k in new[] { 6, 7 })
            {
                list.Add($"DOW{k}");
                list.Add($"LDOW{k}");
                for (int n = 1; n <= 4; n++) list.Add($"NDOW{n}-{k}");
            }
        }
        else if (q.StartsWith('D'))
        {
            int d = int.Parse(q[1..]);
            int block = (d - 1) / 7 + 1;
            if (block <= 4) list.Add($"WOM{block}");
            if (d >= 29) list.Add("LWOM");
            if (d == 31) list.Add("LASTDAY");
        }
        return list;
    }

    /// <summary>Allowed reference time values for a query time, or null for "any".</summary>
    public static IEnumerable<string>? AllowedTimes(TimeSpec? q) => q is null ? null : ["_", .. q.QueryValues];

    /// <summary>Lucene RegExp matching R: and S: tokens compatible with the pattern.</summary>
    public static string BuildRegex(RecurringPattern p)
    {
        var sb = new StringBuilder("[RS]:");
        Append(sb, AllowedPeriods(p.Period));
        sb.Append("\\|");
        Append(sb, AllowedDays(p.Day));
        sb.Append("\\|");
        Append(sb, AllowedTimes(p.Time));
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, IEnumerable<string>? values)
    {
        if (values is null)
        {
            sb.Append("[^|]*");
            return;
        }
        sb.Append('(');
        bool first = true;
        foreach (var v in values.Distinct())
        {
            if (!first) sb.Append('|');
            first = false;
            foreach (char c in v)
            {
                if (!char.IsLetterOrDigit(c)) sb.Append('\\');
                sb.Append(c);
            }
        }
        sb.Append(')');
    }
}
