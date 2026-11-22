namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Signature-token generation. Token shapes:
/// <list type="bullet">
/// <item><c>R:{period}|{day}|{time}</c> — recurring pattern a reference specifies, or a date satisfies.</item>
/// <item><c>A:{cell}[|{time}]</c> — calendar cell (Y/Q/M/W/D) of an anchored reference.</item>
/// <item><c>S:{period}|{day}|{time}</c> — pattern a short anchored reference satisfies (for pattern queries).</item>
/// </list>
/// Time slot: <c>_</c> = whole day / any time, <c>*</c> = "at some time that day" (indexed by timed
/// references so date-only queries still match), <c>MO|AF|EV|NI</c>, <c>H{hh}</c>.
/// </summary>
internal static class TemporalSignatures
{
    public const string FormatVersion = "1";

    public static string R(string period, string day, string time) => $"R:{period}|{day}|{time}";

    public static string A(string cell, string time) => time == "_" ? $"A:{cell}" : $"A:{cell}|{time}";

    /// <summary>Tokens for a concrete date-field value (local wall-clock).</summary>
    public static List<string> ForDateValue(DateTime local)
    {
        var day = DateOnly.FromDateTime(local);
        string[] times = ["_", TemporalCalendar.PartOfDay(local.Hour), $"H{local.Hour:00}"];
        var tokens = new List<string>(160);
        foreach (var p in TemporalCalendar.PeriodValues(day))
            foreach (var d in TemporalCalendar.DayValues(day))
                foreach (var t in times)
                    tokens.Add(R(p, d, t));
        tokens.Add(A(TemporalCalendar.YearCell(day), "_"));
        return tokens;
    }

    /// <summary>Time-slot values a query accepts: date-only → whole-day and timed references.</summary>
    public static string[] QueryTimes(TimeSpec? time) =>
        time is null ? ["_", "*"] : ["_", .. time.QueryValues];

    /// <summary>Time-slot values a reference indexes.</summary>
    public static string[] IndexTimes(TimeSpec? time) =>
        time is null ? ["_"] : ["*", .. time.IndexValues];

    /// <summary>Adds the R: and A: tokens a query day matches against a TIMEX field.</summary>
    public static void AddQueryDay(DateOnly day, TimeSpec? time, ISet<string> rTokens, ISet<string> aTokens)
    {
        var times = QueryTimes(time);
        foreach (var p in TemporalCalendar.PeriodValues(day))
            foreach (var d in TemporalCalendar.DayValues(day))
                foreach (var t in times)
                    rTokens.Add(R(p, d, t));
        foreach (var cell in TemporalCalendar.AnchoredCells(day))
            foreach (var t in times)
                aTokens.Add(A(cell, t));
    }

    /// <summary>Tokens indexed for one reference in a TIMEX field.</summary>
    public static IEnumerable<string> ForReference(TimexValue value, int maxSatisfiesSpanDays)
    {
        switch (value.Kind)
        {
            case TimexValueKind.Recurring:
                foreach (var p in value.Patterns)
                    foreach (var t in IndexTimes(p.Time))
                        yield return R(p.Period, p.Day, t);
                break;

            case TimexValueKind.Anchored:
                foreach (var span in value.Spans)
                {
                    var times = IndexTimes(span.Time);
                    foreach (var cell in Tile(span))
                        foreach (var t in times)
                            yield return A(cell, t);

                    if (span.Days <= maxSatisfiesSpanDays)
                    {
                        foreach (var day in span.EachDay())
                            foreach (var p in TemporalCalendar.PeriodValues(day))
                                foreach (var d in TemporalCalendar.DayValues(day))
                                    foreach (var t in times)
                                        yield return $"S:{p}|{d}|{t}";
                    }
                }
                break;
        }
    }

    /// <summary>Coarsest calendar cells that exactly tile a span (Y → Q → M → D; an exact ISO week → W).</summary>
    public static IEnumerable<string> Tile(AnchoredSpan span)
    {
        if (span.Time is not null)
        {
            foreach (var d in span.EachDay())
                yield return TemporalCalendar.DayCell(d);
            yield break;
        }

        if (span.Days == 7 && TemporalCalendar.IsoDayOfWeek(span.Start) == 1)
        {
            yield return TemporalCalendar.WeekCell(span.Start);
            yield break;
        }

        var cursor = span.Start;
        while (cursor < span.End)
        {
            if (cursor.Month == 1 && cursor.Day == 1 && cursor.Year < 9999 && cursor.AddYears(1) <= span.End)
            {
                yield return TemporalCalendar.YearCell(cursor);
                cursor = cursor.AddYears(1);
            }
            else if (cursor.Day == 1 && (cursor.Month - 1) % 3 == 0 && cursor.AddMonths(3) <= span.End)
            {
                yield return TemporalCalendar.QuarterCell(cursor);
                cursor = cursor.AddMonths(3);
            }
            else if (cursor.Day == 1 && cursor.AddMonths(1) <= span.End)
            {
                yield return TemporalCalendar.MonthCell(cursor);
                cursor = cursor.AddMonths(1);
            }
            else
            {
                yield return TemporalCalendar.DayCell(cursor);
                cursor = cursor.AddDays(1);
            }
        }
    }
}
