using System.Text.RegularExpressions;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>A 4-character year field where any digit may be 'X'.</summary>
internal sealed record YearSpec(string Pattern)
{
    public static readonly YearSpec All = new("XXXX");

    public bool IsAll => Pattern == "XXXX";

    public bool IsExact => Pattern.All(char.IsDigit);

    public bool Matches(int year)
    {
        string s = year.ToString("0000");
        for (int i = 0; i < 4; i++)
            if (Pattern[i] != 'X' && Pattern[i] != s[i])
                return false;
        return true;
    }

    /// <summary>
    /// Years this spec denotes. Exact → one year. Leading-X specs ("XX90") resolve to the latest
    /// match at or before <paramref name="anchorYear"/>. Other partial specs ("199X", "19XX") enumerate.
    /// </summary>
    public int[] Resolve(int anchorYear)
    {
        if (IsAll)
            throw new InvalidOperationException("XXXX has no finite year set.");
        if (IsExact)
            return [int.Parse(Pattern)];
        if (Pattern[0] == 'X')
        {
            for (int y = anchorYear; y >= 1; y--)
                if (Matches(y)) return [y];
            return [ResolveAtLeast(anchorYear)];
        }
        return Enumerable.Range(1, 9999).Where(Matches).ToArray();
    }

    /// <summary>Smallest matching year &gt;= <paramref name="minYear"/>.</summary>
    public int ResolveAtLeast(int minYear)
    {
        if (IsExact) return int.Parse(Pattern);
        for (int y = Math.Max(1, minYear); y <= 9999; y++)
            if (Matches(y)) return y;
        throw new FormatException($"No year matches '{Pattern}' after {minYear}.");
    }
}

/// <summary>One parsed (non-range) TIMEX value: year spec + recurring slots + optional time.</summary>
internal sealed record ParsedTimex(YearSpec Year, string Period, string Day, TimeSpec? Time)
{
    public bool HasCalendarPart => !(Period == "_" && Day == "_") || !Year.IsAll;

    public ParsedTimex WithYear(YearSpec year) => this with { Year = year };
}

/// <summary>Parser for the TIMEX3 dialect emitted by Microsoft.Recognizers.Text plus standard TIMEX3 forms.</summary>
internal static class TimexParser
{
    private static readonly Regex DateRx = new(
        @"^(?<y>[0-9X]{4})" +
        @"(?:-(?:" +
            @"(?<m>[0-9X]{2})(?:-(?:W(?<wom>[0-9X]{2})|WXX-(?<mdow>[1-7])(?:-#(?<nth>-?[0-9]))?|(?<d>[0-9X]{2})))?" +
            @"|W(?<woy>[0-9X]{2})(?:-(?:(?<wwe>WE)|(?<wd>[1-7X])))?" +
            @"|(?<s>SP|SU|FA|WI)" +
            @"|Q(?<q>[1-4X])" +
        @"))?" +
        @"(?:T(?<t>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeRx = new(
        @"^(?:(?<pod>MO|AF|EV|NI|DT)|(?<hh>[0-9]{2})(?::?(?<mi>[0-9]{2})(?::?(?<ss>[0-9]{2}(?:\.[0-9]+)?))?)?)(?:Z|[+-][0-9]{2}:?[0-9]{2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeasonRx = new(@"^(SP|SU|FA|WI)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedTimex Parse(string timex)
    {
        string s = timex.Trim().ToUpperInvariant();
        if (s.Length == 0)
            throw new FormatException("Empty TIMEX value.");

        if (s.StartsWith('T'))
            return new ParsedTimex(YearSpec.All, "_", "_", ParseTime(s[1..], timex));

        if (SeasonRx.IsMatch(s))
            return new ParsedTimex(YearSpec.All, "S" + s, "_", null);

        var m = DateRx.Match(s);
        if (!m.Success)
            throw new FormatException($"Unrecognized TIMEX value '{timex}'.");

        string period = "_";
        string day = "_";

        if (Group(m, "m") is { } month && month != "XX")
            period = $"M{Range(month, 1, 12, timex):00}";

        if (Group(m, "d") is { } dom && dom != "XX")
            day = $"D{Range(dom, 1, 31, timex):00}";

        if (Group(m, "wom") is { } wom && wom != "XX")
        {
            int n = Range(wom, 1, 5, timex);
            day = n == 5 ? "LWOM" : $"WOM{n}";
        }

        if (Group(m, "mdow") is { } mdow)
        {
            int k = int.Parse(mdow);
            if (Group(m, "nth") is { } nthText)
            {
                int nth = int.Parse(nthText);
                day = nth switch
                {
                    >= 1 and <= 4 => $"NDOW{nth}-{k}",
                    5 or -1 => $"LDOW{k}",
                    _ => throw new FormatException($"Invalid nth weekday '#{nth}' in '{timex}'."),
                };
            }
            else
            {
                day = $"DOW{k}";
            }
        }

        if (Group(m, "woy") is { } woy && woy != "XX")
            period = $"W{Range(woy, 1, 53, timex):00}";

        if (Group(m, "wwe") is not null)
            day = "WE";

        if (Group(m, "wd") is { } wd && wd != "X")
            day = $"DOW{wd}";

        if (Group(m, "s") is { } season)
            period = "S" + season;

        if (Group(m, "q") is { } q && q != "X")
            period = "Q" + q;

        TimeSpec? time = Group(m, "t") is { } t ? ParseTime(t, timex) : null;

        return new ParsedTimex(new YearSpec(m.Groups["y"].Value), period, day, time);
    }

    public static TimeSpec ParseTime(string text, string original)
    {
        var m = TimeRx.Match(text);
        if (!m.Success)
            throw new FormatException($"Unrecognized time '{text}' in TIMEX '{original}'.");
        if (Group(m, "pod") is { } pod)
            return TimeSpec.FromPartOfDay(pod);
        int hour = Range(m.Groups["hh"].Value, 0, 24, original);
        int? minute = Group(m, "mi") is { } mi ? Range(mi, 0, 59, original) : null;
        return TimeSpec.FromClock(hour, minute);
    }

    private static string? Group(Match m, string name) => m.Groups[name].Success ? m.Groups[name].Value : null;

    private static int Range(string value, int min, int max, string original)
    {
        if (!int.TryParse(value, out int n) || n < min || n > max)
            throw new FormatException($"Value '{value}' out of range in TIMEX '{original}'.");
        return n;
    }
}
