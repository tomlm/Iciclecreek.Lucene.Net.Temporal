namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Time-of-day part of a TIMEX value: a set of minute-of-day intervals, remembering whether it was
/// written as a part of day (MO/AF/EV/NI/DT) or as clock time.
/// </summary>
public sealed class TimeSpec : IEquatable<TimeSpec>
{
    private static readonly Dictionary<string, (int Start, int End)[]> PodIntervals = new()
    {
        ["MO"] = [(360, 720)],
        ["AF"] = [(720, 1020)],
        ["EV"] = [(1020, 1260)],
        ["NI"] = [(0, 360), (1260, 1440)],
        ["DT"] = [(360, 1020)],
    };

    private TimeSpec((int Start, int End)[] intervals, string[] pods, string key)
    {
        Intervals = intervals;
        Pods = pods;
        Key = key;
    }

    /// <summary>Minute-of-day intervals, end exclusive (0..1440).</summary>
    internal IReadOnlyList<(int Start, int End)> Intervals { get; }

    /// <summary>Parts of day as written (empty for clock times).</summary>
    internal IReadOnlyList<string> Pods { get; }

    internal string Key { get; }

    internal static TimeSpec FromPartOfDay(string pod)
    {
        pod = pod.ToUpperInvariant();
        if (!PodIntervals.TryGetValue(pod, out var intervals))
            throw new FormatException($"Unknown part of day '{pod}'.");
        // DT (daytime) is indexed as morning + afternoon.
        string[] pods = pod == "DT" ? ["MO", "AF"] : [pod];
        return new TimeSpec(intervals, pods, pod);
    }

    internal static TimeSpec FromClock(int hour, int? minute)
    {
        if (hour is < 0 or > 24 || minute is < 0 or > 59)
            throw new FormatException($"Invalid time {hour}:{minute}.");
        if (hour == 24) hour = 0;
        int start = hour * 60 + (minute ?? 0);
        int end = minute.HasValue ? start + 1 : start + 60;
        return new TimeSpec([(start, end)], [], minute.HasValue ? $"{hour:00}:{minute:00}" : $"{hour:00}");
    }

    /// <summary>Interval [start, end) in minutes; wraps past midnight when end &lt;= start.</summary>
    internal static TimeSpec FromInterval(int startMinute, int endMinute)
    {
        (int, int)[] intervals = endMinute > startMinute
            ? [(startMinute, endMinute)]
            : [(0, endMinute), (startMinute, 1440)];
        intervals = intervals.Where(i => i.Item2 > i.Item1).ToArray();
        return new TimeSpec(intervals, [], $"{startMinute}-{endMinute}");
    }

    internal bool IsWholeDay => Intervals.Count == 1 && Intervals[0] == (0, 1440);

    internal int FirstMinute => Intervals.Min(i => i.Start);

    internal int LastMinute => Intervals.Max(i => i.End);

    /// <summary>Hours touched by the intervals.</summary>
    internal IEnumerable<int> Hours => Intervals
        .SelectMany(i => Enumerable.Range(i.Start / 60, (i.End + 59) / 60 - i.Start / 60))
        .Distinct()
        .Order();

    /// <summary>Time-slot values written into the index for a reference with this time.</summary>
    internal IEnumerable<string> IndexValues => Pods.Count > 0 ? Pods : Hours.Select(h => $"H{h:00}");

    /// <summary>Time-slot values a query with this time accepts (overlapping hours and parts of day).</summary>
    internal IEnumerable<string> QueryValues => Hours
        .SelectMany(h => new[] { $"H{h:00}", TemporalCalendar.PartOfDay(h) })
        .Concat(Pods)
        .Distinct();

    public bool Equals(TimeSpec? other) => other is not null && other.Key == Key;

    public override bool Equals(object? obj) => Equals(obj as TimeSpec);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Key;
}
