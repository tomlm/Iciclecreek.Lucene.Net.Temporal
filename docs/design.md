# Iciclecreek.Lucene.Net.Temporal — design spec

## Goal

Let an application **store temporal references in a Lucene.NET 4.8 index and filter against them**, where either side may be partial or recurring.

1. **TIMEX fields** — a document carries one or more TIMEX references, typically extracted from or authored with its text:
   "We meet the last week of every month" → `XXXX-XX-W05`; "on the 1st of every month" → `XXXX-XX-01`; "every Thursday morning" → `XXXX-WXX-4TMO`; "May 1–10" → `(2026-05-01,2026-05-10,P9D)`.
2. **Date fields** — a document has concrete date properties (`Created`, `Sent`) that should be filterable with partial TIMEX patterns: `created:XXXX-12-25` → every doc created on Christmas, any year.
3. **Queries are TIMEX values too** — whole dates (`2026-11-19`) or partial ones (`2026-11-W05` = "last week of this month", `XXXX-12-25`, `2026-SU`, `[2026-11 TO 2026-12]`), against either field kind.

Target: Lucene.NET 4.8.0-beta (port of Java Lucene 4.8). No `DateRangePrefixTree`, no range fields — not needed.

Out of scope: extracting TIMEX from text. That is upstream (Microsoft.Recognizers.Text.DateTime, HeidelTime, SUTime, an LLM, or a human author). This library covers parsing, normalization, indexing and querying.

## Input dialect and parsing

The input dialect is **TIMEX3 as produced by Microsoft.Recognizers.Text** (it is the most likely upstream), plus a few standard TIMEX3 forms it doesn't emit:

| Form | Example | Meaning |
|---|---|---|
| `YYYY[-MM[-DD]]` | `2026-11-19`, `XXXX-12-25`, `XXXX-XX-01` | date, `X` allowed in any digit |
| `YYYY-Www[-D]` | `2026-W47`, `XXXX-WXX-4` | ISO week / weekday |
| `YYYY-MM-Wnn` | `2026-11-W05`, `XXXX-05-W01` | week of month (see *Week of month*) |
| `YYYY-MM-WXX-D-#n` | `XXXX-11-WXX-4-#1` | nth weekday of month |
| `…-WE` | `2026-W47-WE` | weekend |
| `YYYY-(SP\|SU\|FA\|WI)` | `2026-SU`, `XXXX-WI` | season |
| `YYYY-Qn` | `2026-Q2` | quarter (Recognizers emits quarters as ranges instead) |
| `T(HH[:mm[:ss]]\|MO\|AF\|EV\|NI)` | `T10`, `TMO`, `XXXX-WXX-2TMO` | time / part of day, standalone or suffixed |
| `(start,end,duration)` | `(2026-04-01,2026-07-01,P3M)` | range, end exclusive |
| partial years | `199X`, `19XX-12-25`, `(XX90-01-01,XX00-01-01,P10Y)` | decade/century; `XX` century resolved against the anchor (most recent ≤ anchor) |
| `P1WE` | | SET "every weekend" |

A TIMEX may also have a `Mod` (`before`, `after`, `since`, `until`, `approx`, `start`, `mid`, `end`), which Recognizers returns separately from the value. The API takes it as an optional parameter.

**We write our own parser** (it is a small grammar). `Microsoft.Recognizers.Text.DataTypes.TimexExpression` is a **test-only** dependency, not a runtime one. Probing v1.8.13 showed its `TimexProperty` parser loses or misreads several forms we need:

| Input | `TimexProperty` result |
|---|---|
| `2026-Q2` | Year=2026 only; quarter dropped |
| `XXXX-11-WXX-4-#1` (first Thursday of Nov) | WeekOfMonth=4; becomes "4th week of Nov" |
| `XXXX-05-WXX-1` | WeekOfMonth=1 |
| `XXXX-XX-W05`, `XXXX-WXX-WE`, `199X`, `19XX-12-25` | fails (empty) |
| `(XX90-01-01,XX00-01-01,P10Y)` | duration P10Y only |
| `2026-11-W05` | recognizer says Nov 23–30 for "last week of this month", but `TimexResolver` resolves the same string to Nov 30–Dec 7 |

The tests use Recognizers as a **corpus source**: run real phrases through `DateTimeRecognizer` and assert our parser accepts every TIMEX it emits.

Known upstream limitations (document these for users; this library can't fix them): Recognizers splits "last week of every month" into `2026-W46` + `P1M`, resolves "last Friday of every month" to one anchored date, emits `2026-11` typed `set` for "end of every month", and drops the interval from "every other Thursday". For headline recurring cases the TIMEX may need to come from an LLM or the author.

## Core idea: signature tokens

Every temporal fact becomes **one keyword token** in a multi-valued field, so multiple references in one doc never combine. Do NOT use separate start/end numeric fields or per-component fields for TIMEX fields. With several references per doc they produce false matches: the start of one reference pairs with the end of another, or `D:01` from one reference plus `DOW:4` from another matches dates neither one means.

Matching always works the same way: expand one side into a set of tokens, then look the other side's tokens up with an OR of `TermQuery`s (or a `TermsFilter` for large sets).

### Recurring tokens (`R:`) — three slots

A recurring pattern constrains up to three independent slots. The token encodes all three, with `_` for unconstrained, so a compound pattern such as "first Thursday in November" stays **one** token:

```
R:{period}|{day}|{time}
```

| Slot | Values |
|---|---|
| period | `_` · `M{MM}` month · `Q{q}` quarter · `S{SP,SU,FA,WI}` season (meteorological: MAM/JJA/SON/DJF) · `W{ww}` ISO week-of-year |
| day | `_` · `D{dd}` · `DOW{1-7}` (ISO, Mon=1) · `NDOW{n}-{dow}` nth weekday (n=1–4) · `LDOW{dow}` last weekday of month · `WOM{n}` week of month (n=1–4) · `LWOM` last week of month · `LASTDAY` · `WE` weekend |
| time | `_` whole day / any time · `*` "at some time that day" · `{MO,AF,EV,NI}` part of day (MO 06–12, AF 12–17, EV 17–21, NI otherwise; DT = MO+AF) · `H{HH}` hour |

Examples:

| TIMEX | Token |
|---|---|
| `XXXX-12-25` | `R:M12\|D25\|_` |
| `XXXX-XX-01` | `R:_\|D01\|_` |
| `XXXX-WXX-4` | `R:_\|DOW4\|_` |
| `XXXX-WXX-2TMO` | `R:_\|DOW2\|MO` |
| `XXXX-11-WXX-4-#1` | `R:M11\|NDOW1-4\|_` |
| `XXXX-XX-W05` | `R:_\|LWOM\|_` |
| `XXXX-SU` | `R:SSU\|_\|_` |
| `TMO` | `R:_\|_\|MO` |
| `T10` | `R:_\|_\|H10` |
| `P1WE` | `R:_\|WE\|_` |

**Time slot rules.** A reference with a time indexes its time values *plus* `*`; one without indexes `_`. A date-only query accepts `{_, *}`, so it matches both whole-day and timed references. A query with a time accepts `_` plus the hours and parts of day it overlaps (not `*`). So "Nov 19" matches "Nov 19 at 10am", but "Nov 19 at 3pm" doesn't. Anchored `A:` tokens use the same suffixes (`A:D2026-11-19|*`, `A:D2026-11-19|H10`).

### Week of month and "last"

Weeks of the month are **7-day blocks counted from the 1st**: WOM1 = days 1–7, WOM2 = 8–14, and so on. This matches NDOW: the first Thursday is always the Thursday in WOM1.

- `W05`, and therefore "last week of the month", means **the last 7 days of the month** (`LWOM`). A literal fifth block (days 29–31) is rarely meant.
- `#5`, and therefore "last Friday", means `LDOW`. Recognizers emits `#5` for "last Friday of the month". In months with five of that weekday, the fifth one is the last.
- `LWOM` holds exactly when `LDOW` would: `d.Day + 7 > DaysInMonth`.

This differs slightly from Recognizers' own resolution of "last week of this month" (the last full Mon–Sun week). We normalize to one definition. See *Open questions*.

### Anchored tokens (`A:`)

A calendar cell, optionally with a time slot:

| Token | Example |
|---|---|
| `A:Y{yyyy}` | `A:Y2026` |
| `A:Q{yyyy}-{q}` | `A:Q2026-2` |
| `A:M{yyyy}-{MM}` | `A:M2026-05` |
| `A:W{isoYear}-{ww}` | `A:W2026-53` (ISO week-year, not calendar year) |
| `A:D{yyyy-MM-dd}` | `A:D2026-11-19` |
| `…\|{pod}` / `…\|H{HH}` | `A:D2026-11-19\|MO` |

### Satisfies tokens (`S:`)

These go only on **anchored references in TIMEX fields** with spans of 31 days or less. They are the `R:` signature of each day in the span with an `S:` prefix, and they let a pattern query such as "Christmas" find an anchored reference to Dec 25, 2026. They use a separate prefix so a concrete query date of 2025-12-25 can't match an anchored 2026-12-25 reference through them.

## The core functions

### `Signatures(LocalDate d, TimeOnly? t)` — everything a concrete day/time satisfies

- `R:` — the cross product of every period value the day satisfies (`_`, its `M`, `Q`, `S`, `W`) × every day value (`_`, `D`, `DOW`, `NDOW` if nth ≤ 4, `LDOW`/`LWOM` if in last 7 days, `WOM` if ≤ 4, `LASTDAY`, `WE` if Sat/Sun) × time (`_`, plus pod and `H` when `t` is given). That is about 5 × 8 × 3 ≈ 120 tokens per day, which is trivial for Lucene.
- `A:` — `Y`, `Q`, `M`, `W`, `D`, plus `|pod` and `|H` variants when `t` is given.

```csharp
int dow  = ((int)d.DayOfWeek + 6) % 7 + 1;   // ISO Mon=1
int nth  = (d.Day - 1) / 7 + 1;              // NDOW n, WOM n
bool last7 = d.Day + 7 > DateTime.DaysInMonth(d.Year, d.Month);   // LDOW, LWOM
```

### `Normalize(string timex, string? mod, TemporalContext ctx)` → `TimexValue`

Parses the TIMEX and classifies it as:
- **Recurring**: no year and not a relative reference, or `type == set`. Produces one `R:` token. Bare durations (`P1M`, `P3M`, `P1D`) are not indexable and only stored raw, except `P1WE`.
- **Anchored**: a list of concrete wall-clock spans `[start, end)`. Relative values (`PRESENT_REF`, and `XXXX-…` values that the caller marks as relative) resolve against `ctx.Anchor`. Partial years produce several spans: `199X` is one decade span, `19XX-12-25` is 100 one-day spans.
- **Open-ended**: `Mod` is before/after/since/until. Not indexable in a TIMEX field in v1 (stored raw only); usable as a query against date fields.

`approx`/`start`/`mid`/`end` mods are ignored in v1: the reference is treated as the whole base period, which over-matches.

`TemporalContext { TimeZoneInfo Zone; DateTimeOffset Anchor; }` — every TIMEX is wall-clock in `Zone`. The anchor is the doc's sent/created time, or "now" at query time.

## Public API: strategies

The library follows the **Lucene.Net.Spatial pattern**. One logical field maps to several physical fields and several kinds of query, so each logical field is owned by a *strategy* object. The strategy knows its physical field layout, creates the indexable fields and builds queries. Lucene users already know this shape from `SpatialStrategy`:

```csharp
// Spatial:   doc.Add(strategy.CreateIndexableFields(shape));   strategy.MakeQuery(args)
// Temporal:  same shape
var created = new TemporalDateStrategy("created", options);
var when    = new TimexStrategy("when", options);

foreach (var f in created.CreateIndexableFields(msg.Created, senderZone)) doc.Add(f);
foreach (var f in when.CreateIndexableFields(mentions, new TemporalContext(senderZone, msg.Sent))) doc.Add(f);

Query q1 = created.MakeQuery("XXXX-12-25", ctx);
Query q2 = when.MakeQuery("2026-11-W05", ctx);           // "last week of this month"
Query q3 = when.MakeRangeQuery("2026-11", "2026-12", true, true, ctx);
```

```csharp
public abstract class TemporalStrategy
{
    protected TemporalStrategy(string fieldName, TemporalOptions? options = null);
    public string FieldName { get; }
    public TemporalOptions Options { get; }   // DefaultZone, RecurrenceHorizon, MaxSatisfiesSpanDays, IgnoreInvalidReferences

    public TimexQuery MakeQuery(string timex, TemporalContext ctx, string? mod = null);
    public TimexQuery MakeRangeQuery(string? lower, string? upper, bool includeLower, bool includeUpper, TemporalContext ctx);
    public Filter MakeFilter(string timex, TemporalContext ctx, string? mod = null);   // QueryWrapperFilter over MakeQuery

    // Called by TimexQuery.Rewrite; builds the concrete Lucene query from the normalized value.
    protected internal abstract Query BuildQuery(TimexValue value);   // value is already normalized against the context
}

public sealed class TemporalDateStrategy : TemporalStrategy
{
    public Field[] CreateIndexableFields(DateTimeOffset value, TimeZoneInfo? zone = null);
}

public sealed class TimexStrategy : TemporalStrategy
{
    // All of a document's references are passed together with one context, which
    // enforces "one anchor per field per doc" by construction.
    public Field[] CreateIndexableFields(IEnumerable<TimexReference> references, TemporalContext ctx);
    public Field[] CreateIndexableFields(TimexReference reference, TemporalContext ctx);
}

public readonly record struct TimexReference(string Timex, string? Mod = null);
public sealed record TemporalContext(TimeZoneInfo Zone, DateTimeOffset Anchor);
```

Extension methods on `Document` are **thin shortcuts** over the strategy. They follow Lucene.NET 4.8's `doc.AddStringField(...)` convention:

```csharp
doc.AddTemporalDateField(created, msg.Created, senderZone);
doc.AddTimexField(when, mentions, ctx);
```

If `CreateIndexableFields` / `AddTimexField` is called more than once for the same field on one doc, it checks the existing `{field}$anchor` value on the `Document` and throws if the anchor differs.

### `TimexQuery`: a rewrite-based query

`MakeQuery` returns a `TimexQuery`, not the expanded query. Like `MultiTermQuery` and the spatial queries, it holds the logical request and expands itself in `Rewrite(IndexReader)`:

- **Holds:** the strategy, the **normalized** `TimexValue` and the context. Relative values ("this month", `PRESENT_REF`) are resolved against `ctx.Anchor` *at construction*, so a query object means the same thing whenever it runs.
- **`Rewrite`** → `strategy.BuildQuery(...)`, which produces the `ConstantScoreQuery` / `BooleanQuery` / `TermsFilter` / `NumericRangeQuery` / `RegexpQuery` combination described under *Querying*. Boost is carried over.
- **`ToString(field)`** → `when:XXXX-XX-W05`, `created:[2026-11 TO 2026-12]`, not a 400-clause boolean. Logs and `Explain` stay readable.
- **`Equals`/`GetHashCode`** on (field, normalized value, mod). Used for query and filter caching and for de-duplicating clauses.

## Indexing

All component and tick values are **local wall-clock** in the doc's zone (the creator's zone if known, else `Options.DefaultZone`), never UTC. Otherwise an email sent at 8pm on Dec 24 in Seattle files as Christmas. Using wall-clock on both the token side and the numeric side keeps the two query paths consistent.

**Signature tokens are one field with a `TokenStream`**, not one `StringField` per token. `CreateIndexableFields` returns a single `Field(name + "$sig", SignatureTokenStream, SignatureFieldType)`. The stream emits each token through `ICharTermAttribute`. The `FieldType` is indexed, tokenized (Lucene requires this for a `TokenStream` field), not stored, `OmitNorms = true`, `IndexOptions = DOCS_ONLY`. One field instance per doc instead of about 120 `StringField`s.

We deliberately don't use an `Analyzer`. Expansion needs a time zone and an anchor that analyzers don't have, and index-side and query-side expansion are not symmetric.

**`TemporalDateStrategy` physical fields** for field `created`:
- `Int64Field("created$ticks", localWallClockTicks)` — for anchored and open-ended range queries.
- `created$sig` token field — `Signatures(localDate, localTime)`.

**`TimexStrategy` physical fields** for field `when`:
- `when$sig` token field holding, for each reference:
  - Recurring → its `R:` token.
  - Anchored → `A:` tokens for the **coarsest cells that tile each span** (greedy Y → Q → M → D; an exact ISO week → `W`). The query side always emits every granularity, so any tiling matches. Add `S:` tokens if the span is 31 days or less. A reference with a time also emits its coarse token, so date-only queries still hit.
- `Int64Field("when$anchor", anchorLocalTicks)`, once per doc. All of a doc's references in this field share its anchor (sent/created date).
- `StoredField("when", rawTimex[+mod])` for every reference, so the index can be rebuilt when normalization rules change.
- `StoredField("when$v", FormatVersion)` — the token-format version, so an index built with older normalization rules can be detected.

## Querying

The query value is parsed by the same `Normalize` and is either **anchored** (one or more spans) or a **pattern** (one `R:` token, possibly plus a partial-year span list).

| | Date field | TIMEX field |
|---|---|---|
| **Anchored** (`2026-11-19`, `2026-11-W05`, `2026-SU`, `[2026-11 TO 2026-12]`, `199X`) | `NumericRangeQuery.NewInt64Range("f$ticks", start, end, true, false)` per span, OR'd | **Overlap** (below) |
| **Pattern** (`XXXX-12-25`, `XXXX-WXX-4TMO`) | `TermQuery("f$sig", R:token)` | **Pattern match** (below) |
| **Partial-year pattern** (`19XX-12-25`) | `R:` token AND OR of `A:Y` terms over the years, in `f$sig` | treated as anchored spans (100 one-day spans) |
| **Open-ended** (`Mod=after`) | one-sided `NumericRangeQuery` | not supported in v1 (throws) |
| `XXXX` alone | `MatchAllDocsQuery` | docs with any `f$sig` token |

### Overlap: anchored query vs TIMEX field

"Does any day in the query span fall on a day the reference covers?" Let `U` be the union of `Signatures(d)` over each day in the query spans (with time variants if the query has a time).

```
ConstantScore(
    ( OR(R: terms in U)  AND  f$anchor <= queryEnd )      // recurrence validity
  OR  OR(A: terms in U)                                   // anchored references: no validity check
)
```

- The validity clause applies **only to the `R:` branch**. It stops "every Thursday" from an email sent in 2027 matching a 2026 date. Applying it to anchored references would wrongly drop an email sent in 2026 about Christmas 2025.
- Optional horizon (`TemporalOptions.RecurrenceHorizon`, e.g. 1 year) adds `f$anchor >= queryStart - horizon`, so a 2019 "every Thursday" stops matching 2026. Off by default.
- Term sets always go through a `TermsFilter` (Lucene.Net.Queries) wrapped in `ConstantScoreQuery`, so `BooleanQuery.MaxClauseCount` never applies. A one-month query is a few hundred distinct terms.
- Spans of more than ~4 years (and open-ended bounds): the `R:` branch becomes `PrefixQuery("f$sig", "R:")`, which over-matches only on degenerate patterns such as Feb 29 in non-leap years. The `A:` branch becomes one `TermRangeQuery` per cell granularity (`A:Y2020`..`A:Y2026|￿`, and the same for Q/M/W/D). Cell tokens are zero-padded so they sort chronologically, which makes the cost independent of span length and supports open ends (`mod=after`, `[2026-06 TO *]`).
- Wrap in `ConstantScoreQuery`, so references matching many signatures don't outscore single hits.

Example: the doc "We meet the last week of every month" has `R:_|LWOM|_`. The query "last week of this month" = `2026-11-W05` = Nov 24–30. Every day in that span emits `R:_|LWOM|_`, so it matches. A query for 2026-11-20 doesn't.

### Pattern match: pattern query vs TIMEX field

"Is the reference **comparable** to the query on every slot?" Per slot, a reference value matches if it is `_`, equal to the query value, or related to it by implication in either direction (it implies the query value or is implied by it). It doesn't match if the two merely co-occur sometimes: `D25` vs `DOW5` is no match, otherwise "Christmas" would match "every Friday".

Implication lattice:
- period: `_` ⊃ `Q{q}` ⊃ its `M`s; `_` ⊃ `S{s}` ⊃ its `M`s; `_` ⊃ `W{ww}`
- day: `_` ⊃ `DOW{k}` ⊃ `NDOW{n}-{k}`, `LDOW{k}`; `_` ⊃ `WOM{n}` ⊃ `NDOW{n}-*`, `D` in block; `_` ⊃ `LWOM` ⊃ `LDOW*`, `LASTDAY`; `_` ⊃ `WE` ⊃ `DOW6`, `DOW7`
- time: `_` ⊃ pod ⊃ `H` in its range

Implementation: one `RegexpQuery` on `f$sig` over `R:` built from the allowed values per slot (escape `|`), e.g. "Thursdays": `R:[^|]*\|(_|DOW4|NDOW[1-4]-4|LDOW4|WE…)\|[^|]*`. OR it with `TermQuery("f$sig", "S:" + exactToken)` for anchored references. No validity clause applies.

Example: query `XXXX-XX-W05` matches "last week of every month" (`LWOM`), "last Friday of every month" (`LDOW5` ⇒ `LWOM`), "every day" (`_`), and an anchored "Nov 27, 2026" (`S:_|LWOM|_`). It doesn't match "every 15th".

## Query-parser integration

Subclass classic `QueryParser` (`Lucene.Net.QueryParsers.Classic`). The registry is the strategies themselves, so each one knows how to query its own field:

```csharp
public class TemporalQueryParser : QueryParser
{
    readonly IReadOnlyDictionary<string, TemporalStrategy> _strategies;   // keyed by FieldName
    readonly TemporalContext _ctx;   // zone + "now" for relative query values

    public TemporalQueryParser(LuceneVersion v, string defaultField, Analyzer a,
                               IEnumerable<TemporalStrategy> strategies, TemporalContext ctx);

    protected override Query GetFieldQuery(string field, string text, bool quoted) =>
        _strategies.TryGetValue(field, out var s) ? s.MakeQuery(text, _ctx)
                                                  : base.GetFieldQuery(field, text, quoted);

    protected override Query GetRangeQuery(string field, string lo, string hi, bool incLo, bool incHi) =>
        _strategies.TryGetValue(field, out var s) ? s.MakeRangeQuery(lo, hi, incLo, incHi, _ctx)
                                                  : base.GetRangeQuery(field, lo, hi, incLo, incHi);
    // also override the (field, text, int slop) overload
}
```

- Range endpoints are TIMEX cells: `created:[2026-11 TO 2026-12]` runs from the start of the first cell to the end of the last, with endpoint inclusivity applied to the cells.
- Parse TIMEX case-insensitively ourselves. `LowercaseExpandedTerms` only affects wildcard, prefix and range terms in 4.8, but we bypass analysis anyway.
- Hyphens inside terms are fine in the classic grammar. Colons are not: `T10:00` must be quoted or escaped. Parentheses and commas in range-form TIMEX must be quoted: `when:"(2026-05-01,2026-05-10,P9D)"`.
- `GetFieldQuery`/`GetRangeQuery` are `protected internal virtual`; override them from another assembly with `protected override`.

The strategies are the primary surface; the parser and the `Document` extension methods are thin layers over them. Range endpoints may be `*` (open), which maps to `null` in `MakeRangeQuery`. LINQ helpers for LottaDB (`.MatchesTimex(...)`, `.WhereMentions(...)`) belong in LottaDB or an adapter package, so this library depends only on Lucene.NET.

## Packages

- Runtime: `Lucene.Net` 4.8.0-beta, `Lucene.Net.Queries` (`TermsFilter`), `Lucene.Net.QueryParser`.
- Tests only: `Microsoft.Recognizers.Text.DateTime` (corpus of real TIMEX output).

## Open questions

- **"Last week of the month"**: last 7 days (chosen) vs. last full Mon–Sun week (Recognizers' phrase resolution).
- **Seasons**: meteorological vs. astronomical; southern hemisphere (make it a `TemporalContext` option?). Which year owns winter: `2026-WI` = Dec 2026–Feb 2027 (proposed).
- **Explicit recurrence bounds** ("every Thursday until June"): per-reference bounds would need child docs (`Lucene.Net.Join`) to avoid cross-reference contamination; v1 supports only the per-field anchor plus a horizon.
- **Interval recurrences** ("every other Thursday", `freq`): v1 degrades to the base pattern, which over-matches. A parity token (`R:…|BIWK{0,1}`) is possible later.

## Tests to write

Dates below are verified for 2026: Jan 1 is a Thursday; 2026 has 53 ISO weeks.

**Parsing**
- Every TIMEX that `DateTimeRecognizer` emits for a phrase corpus parses without loss (quarters, `#n`, `-WE`, `XX90` ranges, `Mod`).
- `2026-Q2` keeps its quarter; `XXXX-11-WXX-4-#1` → `R:M11|NDOW1-4|_`; `#5` → `LDOW`; `XXXX-XX-W05` → `LWOM`.

**Date fields**
- `XXXX-12-25` matches docs created Dec 25 across several years, not Dec 24/26.
- An 8pm Dec 24 Seattle doc does not match `XXXX-12-25` **or** `2026-12-25` (same zone semantics on both paths).
- `19XX-12-25`, `199X`, `2026-W47`, `2026-SU`, `XXXX-WXX-7`, `XXXX-XX-XXTMO`, `[2026-11 TO 2026-12]`.
- ISO week-year: 2026-12-31 and 2027-01-01 are both `A:W2026-53`.

**TIMEX fields: anchored query (overlap)**
- `XXXX-XX-01` matches 2026-05-01 and 2026-06-01, not 2026-05-02.
- `XXXX-WXX-4` matches any Thursday; `XXXX-11-WXX-4-#1` matches 2026-11-05, not 2026-11-12; last Friday matches 2026-11-27.
- `XXXX-XX-W05` matches the query `2026-11-W05` and 2026-11-24, not 2026-11-23; in February it matches 2026-02-22, not 2026-02-21.
- Anchored `2026-11-26` (Recognizers' "next Thursday" from 2026-11-17) matches only that date.
- **Multiple references in one doc**: `XXXX-XX-01` + `XXXX-WXX-2` does NOT match Wed 2026-04-15.
- **Compound**: "every Monday in May" (`R:M05|DOW1|_`) matches 2026-05-04, not 2026-06-01 (a Monday) or 2026-05-05.
- **Validity**: a recurrence anchored 2027-01-10 doesn't match 2026-05-01; an anchored "2025-12-25" reference in an email sent 2026 **does** match 2025-12-25.
- Horizon option: a 2019 "every Thursday" stops matching 2026 when the horizon is 1y.
- Time: `XXXX-WXX-2TMO` matches 2026-11-17 (date only) and 2026-11-17T09:00, not 2026-11-17T15:00.
- Range query for May 2026 uses `TermsFilter`; a ≥1-year range uses the `R:` prefix path.

**TIMEX fields: pattern query**
- `XXXX-XX-W05` matches `LWOM`, `LDOW5` and `_` references plus anchored 2026-11-27; it doesn't match `XXXX-XX-15`.
- `XXXX-12-25` matches anchored 2026-12-25 (via `S:`) and does not match "every Friday".
