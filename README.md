# Iciclecreek.Lucene.Net.Temporal

TIMEX-based temporal indexing and filtering for **Lucene.NET 4.8**.

Store temporal references that are whole, partial, or recurring ("the last week of every month", "every Thursday morning", "Christmas", "May 1–10"), then filter them with whole or partial dates ("last week of this month", `2026-11-19`, `XXXX-12-25`, `[2026-11 TO 2026-12]`).

```
dotnet add package Iciclecreek.Lucene.Net.Temporal
```

## The problem

People rarely write about time as absolute timestamps. An email says *"we meet the last week of every month"*, a note says *"every Tuesday morning"*, a plan says *"sometime in Q2"*, a memo says *"the 1st of every month"*. These references are **partial**, missing a year, a month or a day, and often **recurring**.

Search engines only understand absolute values. A date field holds one instant, and a range query asks whether that instant falls between two others. That leaves two common questions with no good answer:

- **"What's happening the last week of this month?"** The document that says "last week of every month" contains no date in that range, so a range filter can't find it. Full-text search on "last week" finds the wrong documents.
- **"What was created on Christmas, in any year?"** or **"…on a Monday morning?"** A range query can't express "any year". Elasticsearch, Solr and similar engines push you toward scripts that scan every document, or toward day-of-week and month-day fields you compute yourself.

TIMEX3, the standard notation used by temporal taggers such as Microsoft.Recognizers.Text, HeidelTime and SUTime, already represents these partial references precisely. `XXXX-XX-W05` is "last week of every month". `XXXX-WXX-2TMO` is "Tuesday mornings". `XXXX-12-25` is "Christmas, any year". `2026-Q2` is a quarter. Extractors produce these values, but no search engine can index or filter on them.

**This library closes that gap for Lucene.NET.** It turns each TIMEX value, whether whole, partial or recurring, into signature tokens that Lucene can match with ordinary term queries. Either side of a search can then be partial:

| Content says | TIMEX stored | Query |
|---|---|---|
| "we meet the last week of every month" | `XXXX-XX-W05` | last week of this month (`2026-11-W05`) |
| "every Tuesday morning" | `XXXX-WXX-2TMO` | Tue Nov 17 2026, 9:30am |
| "first Thursday of the month" | `XXXX-XX-WXX-4-#1` | any Thursday (`XXXX-WXX-4`) |
| email sent Dec 25, 2024 | date field | created on Christmas (`XXXX-12-25`) |

It runs as plain keyword and numeric fields with standard Lucene queries, so no custom index format or scoring is involved. Filtering costs about the same as keyword search.

Targets .NET 8 and .NET 10.

## Two kinds of field

| Strategy | Holds | Example query |
|---|---|---|
| `TemporalDateStrategy` | a concrete date property (`Created`, `Sent`) | `created:XXXX-12-25`: created on Christmas, any year |
| `TimexStrategy` | TIMEX references, usually extracted from the document's text | `when:2026-11-W05`: references that fall in the last week of Nov 2026 |

The API follows Lucene.Net.Spatial's strategy pattern: one strategy object per logical field creates the indexable fields and builds the queries.

## Indexing

```csharp
var created = new TemporalDateStrategy("created");
var when    = new TimexStrategy("when");

var doc = new Document();
doc.AddTemporalDateField(created, email.Sent, senderZone);

// TIMEX values from your extractor (Microsoft.Recognizers.Text, HeidelTime, an LLM, ...).
// The context anchor is the document's sent/created time.
var ctx = new TemporalContext(senderZone, email.Sent);
doc.AddTimexField(when, new TimexReference[]
{
    "XXXX-XX-W05",                                  // "we meet the last week of every month"
    "XXXX-WXX-2TMO",                                // "every Tuesday morning"
    new("2026-12-25", Type: "date"),                // "Christmas"
    new("XXXX-06", Mod: "since", Type: "daterange") // stored; open-ended references aren't indexed in v1
}, ctx);

writer.AddDocument(doc);
```

## Querying

```csharp
var now = TemporalContext.Now(userZone);

searcher.Search(when.MakeQuery("2026-11-W05", now), 10);        // last week of this month
searcher.Search(when.MakeQuery("2026-11-19T09:30", now), 10);   // a specific time
searcher.Search(when.MakeQuery("XXXX-WXX-4", now), 10);         // references about Thursdays
searcher.Search(created.MakeQuery("XXXX-12-25", now), 10);      // created on any Christmas
searcher.Search(created.MakeRangeQuery("2026-11", "2026-12", true, true, now), 10);
searcher.Search(created.MakeQuery("2026-06", now, mod: "after"), 10);
```

Or use the query parser:

```csharp
var parser = new TemporalQueryParser(LuceneVersion.LUCENE_48, "body", analyzer, [created, when], now);
var q = parser.Parse("dinner AND when:2026-11-W05 AND created:[2026-01 TO *]");
```

Quote TIMEX values that contain `:` or `(`. For example: `when:"2026-11-19T09:30"`.

### What matches

- **Concrete date or span against a TIMEX field (overlap).** A reference matches if it covers any day in the query span. "Every Thursday" matches `2026-11-19`. "Last week of every month" matches `2026-11-W05` and `2026-02-22`.
- **Pattern against a TIMEX field.** A reference matches if it agrees with the query on every slot (period, day, time), allowing implication in either direction. Query `XXXX-WXX-4` (Thursdays) matches "first Thursday of the month" and an anchored Thursday. It doesn't match "every Friday" or "the 25th".
- **Recurrence validity.** A recurring reference only matches dates from its anchor date on. A 2027 email's "every Thursday" won't match a date in 2026. To also expire old recurrences, set `TemporalOptions.RecurrenceHorizon`.
- **Time of day.** A date-only query matches both timed and whole-day references. A timed query only matches references whose time overlaps it, plus whole-day references.
- **Time zones.** Every component is computed in local wall-clock time. 8pm on Dec 24 in Seattle is never "Christmas".

## Supported TIMEX forms

| Form | Example |
|---|---|
| Dates with X wildcards | `2026-11-19`, `XXXX-12-25`, `XXXX-XX-01`, `19XX-12-25`, `199X` |
| ISO weeks and weekdays | `2026-W47`, `XXXX-WXX-4`, `2026-W47-WE` |
| Week of month (`W05` = last 7 days) | `2026-11-W05`, `XXXX-XX-W02` |
| Nth / last weekday | `XXXX-XX-WXX-4-#1`, `XXXX-XX-WXX-5-#5` |
| Seasons and quarters | `2026-SU`, `XXXX-WI`, `2026-Q2` |
| Times and parts of day | `T10`, `T10:30`, `TMO`, `XXXX-WXX-2TEV` |
| Ranges | `(2026-05-01,2026-05-10,P9D)`, `(T09,T12,PT3H)`, `(XX90-01-01,XX00-01-01,P10Y)` |
| Sets | `P1WE` (every weekend), `P1D` (every day) |
| References | `PRESENT_REF`, `PAST_REF`, `FUTURE_REF` |
| Mods | `before`, `after`, `since`, `until` (open-ended); others treat the value as its whole period |

A value with an unspecified year is **recurring** when `Type` is null or `set`. For other types (`date`, `daterange`, `time`, ...), it resolves to the occurrence nearest the anchor, the way Microsoft.Recognizers.Text reports "Christmas" or "Thursday".

## Limitations

- **Extraction quality decides result quality.** Microsoft.Recognizers.Text, for example, splits "last week of every month" into `2026-W46` + `P1M`, and drops the interval from "every other Thursday". For recurring phrases, an LLM extractor or an author-supplied TIMEX works better.
- **Interval recurrences are approximated.** Values like "every other Thursday" index as the base pattern, which over-matches.
- **Open-ended references aren't indexed yet.** "After June" is stored on TIMEX fields but not indexed in v1. Open-ended queries work on both field kinds.
- **One anchor per field per document.** All of a document's references in one field share a single anchor.

See [docs/design.md](docs/design.md) for the full design.
