using System.Globalization;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>How a normalized TIMEX value is interpreted.</summary>
public enum TimexValueKind
{
    /// <summary>Not indexable (e.g. a bare duration such as P1M).</summary>
    None,

    /// <summary>A pattern with no specific year ("every Thursday", "XXXX-12-25").</summary>
    Recurring,

    /// <summary>One or more concrete wall-clock spans ("2026-11", "2026-11-W05", "19XX-12-25").</summary>
    Anchored,

    /// <summary>A one-sided bound (mod before/after/since/until, PAST_REF, FUTURE_REF, open ranges).</summary>
    OpenEnded,
}

/// <summary>A recurring pattern: period, day and time slots ("_" = unconstrained).</summary>
internal sealed record RecurringPattern(string Period, string Day, TimeSpec? Time)
{
    public bool IsUnconstrained => Period == "_" && Day == "_" && Time is null;

    public string Key => $"{Period}|{Day}|{Time?.Key ?? "_"}";
}

/// <summary>Concrete days [Start, End) with an optional time-of-day applying to each day.</summary>
internal sealed record AnchoredSpan(DateOnly Start, DateOnly End, TimeSpec? Time)
{
    public int Days => End.DayNumber - Start.DayNumber;

    public DateTime StartInstant => Start.ToDateTime(TimeOnly.MinValue).AddMinutes(Time?.FirstMinute ?? 0);

    public DateTime EndInstant => Time is null
        ? End.ToDateTime(TimeOnly.MinValue)
        : End.AddDays(-1).ToDateTime(TimeOnly.MinValue).AddMinutes(Time.LastMinute);

    public IEnumerable<DateOnly> EachDay()
    {
        for (var d = Start; d < End; d = d.AddDays(1))
            yield return d;
    }

    public string Key => $"{Start:yyyy-MM-dd}/{End:yyyy-MM-dd}@{Time?.Key ?? "_"}";
}

/// <summary>
/// A TIMEX value normalized against a <see cref="TemporalContext"/>: recurring pattern(s),
/// concrete spans, or an open-ended bound.
/// </summary>
public sealed class TimexValue
{
    private TimexValue(TimexValueKind kind, string source)
    {
        Kind = kind;
        Source = source;
    }

    public TimexValueKind Kind { get; }

    /// <summary>The TIMEX text this value was parsed from.</summary>
    public string Source { get; }

    /// <summary>Local wall-clock start (inclusive) for anchored and open-ended values.</summary>
    public DateTime? Start { get; private set; }

    /// <summary>Local wall-clock end (exclusive) for anchored and open-ended values.</summary>
    public DateTime? End { get; private set; }

    internal IReadOnlyList<RecurringPattern> Patterns { get; private set; } = [];

    internal IReadOnlyList<AnchoredSpan> Spans { get; private set; } = [];

    /// <summary>For partial-year values with a pattern ("19XX-12-25"): the pattern and the years.</summary>
    internal (RecurringPattern Pattern, int[] Years)? PartialYears { get; private set; }

    /// <summary>Canonical identity, used for query equality.</summary>
    internal string Key { get; private set; } = "";

    public override string ToString() => $"{Kind}: {Key}";

    /// <summary>
    /// Parse and normalize a TIMEX value.
    /// </summary>
    /// <param name="timex">TIMEX3 value, e.g. <c>2026-11-19</c>, <c>XXXX-WXX-4</c>, <c>(2026-05-01,2026-05-10,P9D)</c>.</param>
    /// <param name="context">Zone and anchor for relative and unspecified-year values.</param>
    /// <param name="mod">Optional TIMEX3 mod (before, after, since, until, ...).</param>
    /// <param name="type">Optional TIMEX3 type. Null or "set" makes unspecified-year values recurring;
    /// other types (date, daterange, time, ...) resolve them to the occurrence nearest the anchor.</param>
    public static TimexValue Parse(string timex, TemporalContext context, string? mod = null, string? type = null)
    {
        ArgumentNullException.ThrowIfNull(timex);
        ArgumentNullException.ThrowIfNull(context);

        if (string.Equals(type, "duration", StringComparison.OrdinalIgnoreCase))
            return Finish(new TimexValue(TimexValueKind.None, timex));

        bool recurringIfOpenYear = type is null || string.Equals(type, "set", StringComparison.OrdinalIgnoreCase);

        string? m = mod?.Trim().ToLowerInvariant();
        if (m is "before" or "after" or "since" or "until" or "on_or_before" or "on_or_after")
        {
            var b = Build(timex, context, recurringIfOpenYear: false);
            if (b.Start is null && b.End is null)
                throw new FormatException($"TIMEX '{timex}' with mod '{mod}' has no bounds.");
            return m switch
            {
                "before" => OpenEnded(timex, null, b.Start),
                "until" or "on_or_before" => OpenEnded(timex, null, b.End),
                "after" => OpenEnded(timex, b.End, null),
                _ => OpenEnded(timex, b.Start, null), // since, on_or_after
            };
        }

        // approx/start/mid/end/less/more: v1 treats the reference as its whole base period.
        return Build(timex, context, recurringIfOpenYear);
    }

    /// <summary>A range between two TIMEX cells, e.g. [2026-11 TO 2026-12]. Null or "*" means open.</summary>
    public static TimexValue Range(string? lower, string? upper, bool includeLower, bool includeUpper, TemporalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lower = lower is null || lower.Trim() == "*" ? null : lower;
        upper = upper is null || upper.Trim() == "*" ? null : upper;
        string source = $"{(includeLower ? '[' : '{')}{lower ?? "*"} TO {upper ?? "*"}{(includeUpper ? ']' : '}')}";

        DateTime? start = null, end = null;
        if (lower is not null)
        {
            var lo = Bounded(Parse(lower, context, type: "date"), lower);
            start = includeLower ? lo.Start : lo.End;
        }
        if (upper is not null)
        {
            var hi = Bounded(Parse(upper, context, type: "date"), upper);
            end = includeUpper ? hi.End : hi.Start;
        }

        if (start is null && end is null)
            return Recurring(source, [new RecurringPattern("_", "_", null)]);
        if (start is null || end is null)
            return OpenEnded(source, start, end);
        return Anchored(source, SpansFromInstants(start.Value, end.Value, source));
    }

    private static TimexValue Bounded(TimexValue v, string text) =>
        v.Kind == TimexValueKind.Anchored
            ? v
            : throw new FormatException($"Range endpoint '{text}' must be an anchored date.");

    private static TimexValue Build(string timex, TemporalContext ctx, bool recurringIfOpenYear)
    {
        string s = timex.Trim().ToUpperInvariant();
        var anchor = ctx.AnchorDate;

        switch (s)
        {
            case "PRESENT_REF":
                return Anchored(timex, [new AnchoredSpan(anchor, anchor.AddDays(1), null)]);
            case "PAST_REF":
                return OpenEnded(timex, null, anchor.ToDateTime(TimeOnly.MinValue));
            case "FUTURE_REF":
                return OpenEnded(timex, anchor.AddDays(1).ToDateTime(TimeOnly.MinValue), null);
        }

        if (s.StartsWith('('))
            return BuildRange(timex, s, ctx, recurringIfOpenYear);

        if (s.StartsWith('P'))
        {
            return s switch
            {
                "P1WE" => recurringIfOpenYear
                    ? Recurring(timex, [new RecurringPattern("_", "WE", null)])
                    : Anchored(timex, ResolveNearest("_", "WE", null, anchor)),
                "P1D" when recurringIfOpenYear => Recurring(timex, [new RecurringPattern("_", "_", null)]),
                _ => Finish(new TimexValue(TimexValueKind.None, timex)),
            };
        }

        var p = TimexParser.Parse(s);
        if (p.Year.IsAll)
        {
            return recurringIfOpenYear
                ? Recurring(timex, [new RecurringPattern(p.Period, p.Day, p.Time)])
                : Anchored(timex, ResolveNearest(p.Period, p.Day, p.Time, anchor));
        }

        int[] years = p.Year.Resolve(ctx.AnchorLocal.Year);
        var spans = Enumerate(years, p.Period, p.Day, p.Time);
        if (spans.Count == 0)
            throw new FormatException($"TIMEX '{timex}' matches no dates.");
        var value = Anchored(timex, spans, finish: false);
        if (years.Length > 1 && !(p.Period == "_" && p.Day == "_"))
            value.PartialYears = (new RecurringPattern(p.Period, p.Day, p.Time), years);
        return Finish(value);
    }

    private static TimexValue BuildRange(string timex, string s, TemporalContext ctx, bool recurringIfOpenYear)
    {
        if (!s.EndsWith(')'))
            throw new FormatException($"Unrecognized TIMEX range '{timex}'.");
        var parts = s[1..^1].Split(',');
        if (parts.Length < 2)
            throw new FormatException($"Unrecognized TIMEX range '{timex}'.");

        var a = TimexParser.Parse(parts[0]);
        var b = TimexParser.Parse(parts[1]);
        var anchor = ctx.AnchorDate;

        bool aTimeOnly = !a.HasCalendarPart;
        bool bTimeOnly = !b.HasCalendarPart;

        // (T09,T12,PT3H): a time-of-day range.
        if (aTimeOnly && bTimeOnly)
        {
            if (a.Time is null || b.Time is null)
                throw new FormatException($"Unrecognized TIMEX range '{timex}'.");
            var time = TimeSpec.FromInterval(a.Time.FirstMinute, b.Time.FirstMinute);
            return recurringIfOpenYear
                ? Recurring(timex, [new RecurringPattern("_", "_", time)])
                : Anchored(timex, [new AnchoredSpan(anchor, anchor.AddDays(1), time)]);
        }

        // A time-only end inherits the date of the start: (2026-11-19T10,T12,PT2H).
        if (bTimeOnly) b = a with { Time = b.Time };
        if (aTimeOnly) a = b with { Time = a.Time };

        if (a.Year.IsAll && b.Year.IsAll)
        {
            if (recurringIfOpenYear)
                return Recurring(timex, RecurringRange(a, b, timex));

            // Unspecified-year range with a non-set type: nearest occurrence to the anchor.
            List<AnchoredSpan>? best = null;
            long bestDistance = long.MaxValue;
            for (int y = anchor.Year - 1; y <= anchor.Year + 1; y++)
            {
                var start = StartInstant(a, y);
                var end = StartInstant(b, y);
                if (end <= start) end = StartInstant(b, y + 1);
                long distance = Distance(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), anchor);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = SpansFromInstants(start, end, timex);
                }
            }
            return Anchored(timex, best!);
        }

        if (a.Year.IsAll) a = a.WithYear(b.Year);
        if (b.Year.IsAll) b = b.WithYear(a.Year);

        int aYear = a.Year.IsExact ? a.Year.Resolve(0)[0] : a.Year.Resolve(anchor.Year)[0];
        int bYear = b.Year.IsExact ? b.Year.Resolve(0)[0] : b.Year.ResolveAtLeast(aYear);
        var s0 = StartInstant(a, aYear);
        var e0 = StartInstant(b, bYear);
        if (e0 <= s0 && !b.Year.IsExact)
            e0 = StartInstant(b, b.Year.ResolveAtLeast(bYear + 1));
        return Anchored(timex, SpansFromInstants(s0, e0, timex));
    }

    private static List<RecurringPattern> RecurringRange(ParsedTimex a, ParsedTimex b, string timex)
    {
        // Same day pattern with times: (XXXX-WXX-1T09,XXXX-WXX-1T12,PT3H).
        if (a.Period == b.Period && a.Day == b.Day && a.Time is not null && b.Time is not null)
            return [new RecurringPattern(a.Period, a.Day, TimeSpec.FromInterval(a.Time.FirstMinute, b.Time.FirstMinute))];

        // Month-day to month-day: (XXXX-05-01,XXXX-05-10,P9D). Enumerate over a leap reference year.
        if (a.Period.StartsWith('M') && a.Day.StartsWith('D') && b.Period.StartsWith('M') && b.Day.StartsWith('D'))
        {
            var start = new DateOnly(2000, Num(a.Period), Num(a.Day));
            var end = new DateOnly(2000, Num(b.Period), Num(b.Day));
            if (end <= start) end = end.AddYears(1);
            var list = new List<RecurringPattern>();
            for (var d = start; d < end; d = d.AddDays(1))
                list.Add(new RecurringPattern($"M{d.Month:00}", $"D{d.Day:00}", null));
            return list;
        }

        // Month to month: (XXXX-05,XXXX-08,P3M).
        if (a.Period.StartsWith('M') && a.Day == "_" && b.Period.StartsWith('M') && b.Day == "_")
        {
            var list = new List<RecurringPattern>();
            int m = Num(a.Period), end = Num(b.Period);
            do
            {
                list.Add(new RecurringPattern($"M{m:00}", "_", null));
                m = m % 12 + 1;
            } while (m != end);
            return list;
        }

        // Weekday to weekday: (XXXX-WXX-1,XXXX-WXX-5,P4D).
        if (a.Period == "_" && b.Period == "_" && a.Day.StartsWith("DOW") && b.Day.StartsWith("DOW"))
        {
            var list = new List<RecurringPattern>();
            int k = Num(a.Day), end = Num(b.Day);
            do
            {
                list.Add(new RecurringPattern("_", $"DOW{k}", null));
                k = k % 7 + 1;
            } while (k != end);
            return list;
        }

        throw new FormatException($"Unsupported recurring range '{timex}'.");
    }

    private static int Num(string slot) => int.Parse(new string(slot.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);

    private static DateTime StartInstant(ParsedTimex p, int year)
    {
        var spans = Enumerate([year], p.Period, p.Day, p.Time);
        if (spans.Count == 0)
            throw new FormatException($"TIMEX range endpoint matches no dates in {year}.");
        return spans[0].StartInstant;
    }

    /// <summary>Concrete days of the given years that satisfy the pattern, merged into spans.</summary>
    internal static List<AnchoredSpan> Enumerate(IEnumerable<int> years, string period, string day, TimeSpec? time)
    {
        var spans = new List<AnchoredSpan>();
        foreach (int y in years)
        {
            if (period == "_" && day == "_")
            {
                var first = new DateOnly(y, 1, 1);
                spans.Add(new AnchoredSpan(first, y < 9999 ? first.AddYears(1) : DateOnly.MaxValue, time));
                continue;
            }

            var from = y > 1 ? new DateOnly(y - 1, 12, 1) : new DateOnly(1, 1, 1);
            var to = y < 9999 ? new DateOnly(y + 1, 3, 1) : DateOnly.MaxValue; // winter runs into Feb of y+1
            for (var d = from; d < to; d = d.AddDays(1))
            {
                if (TemporalCalendar.PeriodYear(d, period) == y && TemporalCalendar.Matches(d, period, day))
                    spans.Add(new AnchoredSpan(d, d.AddDays(1), time));
            }
        }
        return Merge(spans);
    }

    /// <summary>The run of days satisfying the pattern that is nearest the anchor (ties go to the future).</summary>
    internal static List<AnchoredSpan> ResolveNearest(string period, string day, TimeSpec? time, DateOnly anchor)
    {
        if (period == "_" && day == "_")
            return [new AnchoredSpan(anchor, anchor.AddDays(1), time)];

        DateOnly? bestStart = null, bestEnd = null;
        long bestDistance = long.MaxValue;
        DateOnly? runStart = null;
        var from = anchor.AddDays(-1500);
        var to = anchor.AddDays(1500);
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            bool match = d < to && TemporalCalendar.Matches(d, period, day);
            if (match && runStart is null)
            {
                runStart = d;
            }
            else if (!match && runStart is not null)
            {
                long distance = Distance(runStart.Value, d, anchor);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestStart = runStart;
                    bestEnd = d;
                }
                runStart = null;
            }
        }

        if (bestStart is null)
            throw new FormatException($"Pattern {period}|{day} has no occurrence near {anchor}.");
        return [new AnchoredSpan(bestStart.Value, bestEnd!.Value, time)];
    }

    private static long Distance(DateOnly start, DateOnly endExclusive, DateOnly anchor)
    {
        if (anchor >= start && anchor < endExclusive) return 0;
        return anchor < start ? start.DayNumber - anchor.DayNumber : anchor.DayNumber - endExclusive.DayNumber + 1;
    }

    /// <summary>Spans covering wall-clock [start, end); partial first/last days carry a time interval.</summary>
    internal static List<AnchoredSpan> SpansFromInstants(DateTime start, DateTime end, string source)
    {
        if (end <= start)
            throw new FormatException($"TIMEX range '{source}' is empty or reversed.");

        var sd = DateOnly.FromDateTime(start);
        var ed = DateOnly.FromDateTime(end);
        if (start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == TimeSpan.Zero)
            return [new AnchoredSpan(sd, ed, null)];

        var lastDay = end.TimeOfDay == TimeSpan.Zero ? ed.AddDays(-1) : ed;
        if (lastDay.DayNumber - sd.DayNumber > 3660)
            return [new AnchoredSpan(sd, lastDay.AddDays(1), null)];

        var spans = new List<AnchoredSpan>();
        for (var d = sd; d <= lastDay; d = d.AddDays(1))
        {
            int from = d == sd ? (int)start.TimeOfDay.TotalMinutes : 0;
            int to = d == ed ? (int)end.TimeOfDay.TotalMinutes : 1440;
            if (to <= from) continue;
            var time = from == 0 && to == 1440 ? null : TimeSpec.FromInterval(from, to);
            spans.Add(new AnchoredSpan(d, d.AddDays(1), time));
        }
        return Merge(spans);
    }

    private static List<AnchoredSpan> Merge(List<AnchoredSpan> spans)
    {
        var sorted = spans.OrderBy(s => s.Start).ToList();
        var result = new List<AnchoredSpan>();
        foreach (var s in sorted)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.End == s.Start && Equals(last.Time, s.Time))
                {
                    result[^1] = last with { End = s.End };
                    continue;
                }
            }
            result.Add(s);
        }
        return result;
    }

    private static TimexValue Recurring(string source, List<RecurringPattern> patterns) =>
        Finish(new TimexValue(TimexValueKind.Recurring, source) { Patterns = patterns });

    private static TimexValue Anchored(string source, List<AnchoredSpan> spans, bool finish = true)
    {
        var v = new TimexValue(TimexValueKind.Anchored, source)
        {
            Spans = spans,
            Start = spans.Min(s => s.StartInstant),
            End = spans.Max(s => s.EndInstant),
        };
        return finish ? Finish(v) : v;
    }

    private static TimexValue OpenEnded(string source, DateTime? start, DateTime? end) =>
        Finish(new TimexValue(TimexValueKind.OpenEnded, source) { Start = start, End = end });

    private static TimexValue Finish(TimexValue v)
    {
        v.Key = v.Kind switch
        {
            TimexValueKind.None => "none",
            TimexValueKind.Recurring => "R:" + string.Join(";", v.Patterns.Select(p => p.Key)),
            TimexValueKind.Anchored => "A:" + string.Join(";", v.Spans.Select(s => s.Key)) + (v.PartialYears is null ? "" : "|P"),
            _ => $"O:{v.Start:O}/{v.End:O}",
        };
        return v;
    }
}
