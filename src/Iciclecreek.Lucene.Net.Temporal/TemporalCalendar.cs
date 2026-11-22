using System.Globalization;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>Calendar helpers shared by signature generation, parsing and resolution.</summary>
internal static class TemporalCalendar
{
    public static int IsoDayOfWeek(DateOnly d) => ((int)d.DayOfWeek + 6) % 7 + 1;

    public static int Quarter(int month) => (month - 1) / 3 + 1;

    /// <summary>Meteorological seasons: MAM=SP, JJA=SU, SON=FA, DJF=WI.</summary>
    public static string Season(int month) => month switch
    {
        3 or 4 or 5 => "SP",
        6 or 7 or 8 => "SU",
        9 or 10 or 11 => "FA",
        _ => "WI",
    };

    public static int[] SeasonMonths(string season) => season switch
    {
        "SP" => [3, 4, 5],
        "SU" => [6, 7, 8],
        "FA" => [9, 10, 11],
        "WI" => [12, 1, 2],
        _ => throw new ArgumentOutOfRangeException(nameof(season)),
    };

    public static int IsoWeek(DateOnly d) => ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue));

    public static int IsoWeekYear(DateOnly d) => ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue));

    public static int DaysInMonth(DateOnly d) => DateTime.DaysInMonth(d.Year, d.Month);

    /// <summary>True when the day is in the last 7 days of its month (last weekday / last week).</summary>
    public static bool InLastSevenDays(DateOnly d) => d.Day + 7 > DaysInMonth(d);

    /// <summary>7-day block of the month the day falls in (1 = days 1-7, ...).</summary>
    public static int BlockOfMonth(DateOnly d) => (d.Day - 1) / 7 + 1;

    public static string PartOfDay(int hour) => hour switch
    {
        >= 6 and < 12 => "MO",
        >= 12 and < 17 => "AF",
        >= 17 and < 21 => "EV",
        _ => "NI",
    };

    /// <summary>Every period-slot value a day satisfies, including "_".</summary>
    public static IEnumerable<string> PeriodValues(DateOnly d)
    {
        yield return "_";
        yield return $"M{d.Month:00}";
        yield return $"Q{Quarter(d.Month)}";
        yield return $"S{Season(d.Month)}";
        yield return $"W{IsoWeek(d):00}";
    }

    /// <summary>Every day-slot value a day satisfies, including "_".</summary>
    public static IEnumerable<string> DayValues(DateOnly d)
    {
        int dow = IsoDayOfWeek(d);
        int block = BlockOfMonth(d);
        bool last7 = InLastSevenDays(d);

        yield return "_";
        yield return $"D{d.Day:00}";
        yield return $"DOW{dow}";
        if (block <= 4)
        {
            yield return $"NDOW{block}-{dow}";
            yield return $"WOM{block}";
        }
        if (last7)
        {
            yield return $"LDOW{dow}";
            yield return "LWOM";
        }
        if (d.Day == DaysInMonth(d))
            yield return "LASTDAY";
        if (dow >= 6)
            yield return "WE";
    }

    /// <summary>Calendar cells (without the "A:" prefix) that contain the day.</summary>
    public static IEnumerable<string> AnchoredCells(DateOnly d)
    {
        yield return YearCell(d);
        yield return QuarterCell(d);
        yield return MonthCell(d);
        yield return WeekCell(d);
        yield return DayCell(d);
    }

    public static string YearCell(DateOnly d) => $"Y{d.Year:0000}";
    public static string QuarterCell(DateOnly d) => $"Q{d.Year:0000}-{Quarter(d.Month)}";
    public static string MonthCell(DateOnly d) => $"M{d.Year:0000}-{d.Month:00}";
    public static string WeekCell(DateOnly d) => $"W{IsoWeekYear(d):0000}-{IsoWeek(d):00}";
    public static string DayCell(DateOnly d) => $"D{d.Year:0000}-{d.Month:00}-{d.Day:00}";

    public static bool MatchesPeriod(DateOnly d, string period) => period == "_" || PeriodValues(d).Contains(period);

    public static bool MatchesDay(DateOnly d, string day) => day == "_" || DayValues(d).Contains(day);

    public static bool Matches(DateOnly d, string period, string day) => MatchesPeriod(d, period) && MatchesDay(d, day);

    /// <summary>
    /// The year a day belongs to for a given period: ISO week-year for week periods,
    /// the December year for winter (2026-WI = Dec 2026 - Feb 2027), else the calendar year.
    /// </summary>
    public static int PeriodYear(DateOnly d, string period)
    {
        if (period.StartsWith('W'))
            return IsoWeekYear(d);
        if (period == "SWI" && d.Month <= 2)
            return d.Year - 1;
        return d.Year;
    }
}
