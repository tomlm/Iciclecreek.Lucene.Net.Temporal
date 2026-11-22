namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>
/// Context for interpreting TIMEX values: the wall-clock time zone and the anchor instant
/// (a document's sent/created time when indexing, or "now" when querying).
/// </summary>
public sealed record TemporalContext(TimeZoneInfo Zone, DateTimeOffset Anchor)
{
    /// <summary>Context anchored at the current time.</summary>
    public static TemporalContext Now(TimeZoneInfo? zone = null) => new(zone ?? TimeZoneInfo.Utc, DateTimeOffset.Now);

    /// <summary>The anchor as local wall-clock time in <see cref="Zone"/>.</summary>
    public DateTime AnchorLocal => TimeZoneInfo.ConvertTime(Anchor, Zone).DateTime;

    internal DateOnly AnchorDate => DateOnly.FromDateTime(AnchorLocal);
}

/// <summary>Options shared by temporal strategies.</summary>
public sealed class TemporalOptions
{
    /// <summary>Zone used when a date or context doesn't specify one. Default UTC.</summary>
    public TimeZoneInfo DefaultZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>
    /// When set, recurring references only match query dates within this long after their anchor
    /// (e.g. one year: a 2019 "every Thursday" stops matching 2026). Default: no horizon.
    /// </summary>
    public TimeSpan? RecurrenceHorizon { get; init; }

    /// <summary>Anchored references spanning at most this many days also index S: tokens for pattern queries.</summary>
    public int MaxSatisfiesSpanDays { get; init; } = 31;

    /// <summary>When true, references that fail to parse are skipped at index time instead of throwing.</summary>
    public bool IgnoreInvalidReferences { get; init; }

    internal static readonly TemporalOptions Default = new();
}

/// <summary>
/// A temporal reference to index: a TIMEX value plus the optional TIMEX3 <c>mod</c> and <c>type</c>
/// (as returned by Microsoft.Recognizers.Text). A value with an unspecified year (XXXX) is treated as
/// recurring when <paramref name="Type"/> is null or "set"; for other types it resolves to the occurrence
/// nearest the context anchor.
/// </summary>
public readonly record struct TimexReference(string Timex, string? Mod = null, string? Type = null)
{
    public static implicit operator TimexReference(string timex) => new(timex);

    /// <summary>Compact form used for the stored field: <c>timex[;mod=..][;type=..]</c>.</summary>
    public string ToStoredString()
    {
        var s = Timex;
        if (!string.IsNullOrEmpty(Mod)) s += ";mod=" + Mod;
        if (!string.IsNullOrEmpty(Type)) s += ";type=" + Type;
        return s;
    }

    public static TimexReference FromStoredString(string stored)
    {
        var parts = stored.Split(';');
        string? mod = null, type = null;
        foreach (var p in parts.Skip(1))
        {
            if (p.StartsWith("mod=", StringComparison.Ordinal)) mod = p[4..];
            else if (p.StartsWith("type=", StringComparison.Ordinal)) type = p[5..];
        }
        return new TimexReference(parts[0], mod, type);
    }
}
