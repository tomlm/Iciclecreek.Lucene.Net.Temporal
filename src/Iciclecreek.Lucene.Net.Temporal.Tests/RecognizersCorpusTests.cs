using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace Iciclecreek.Lucene.Net.Temporal.Tests;

/// <summary>Every TIMEX Microsoft.Recognizers.Text emits for a phrase corpus must normalize without error.</summary>
public class RecognizersCorpusTests
{
    private static readonly string[] Phrases =
    [
        "We meet the last week of every month", "last week of this month", "the last week of the month",
        "on the 1st of every month", "every Thursday", "first Thursday of the month", "next Thursday",
        "in May", "every Monday in May", "Christmas", "the 90s", "every morning", "May 1 to May 10",
        "last Friday of every month", "last friday of the month", "end of every month", "every other Thursday",
        "weekday mornings", "summer 2026", "week 47", "every day at 10am", "after June", "since June",
        "before Christmas", "the weekend", "every weekend", "Q2 2026", "the second quarter", "every quarter",
        "every first Monday", "the last day of the month", "every Tuesday morning", "December 2026",
        "last week of May", "tomorrow", "yesterday", "today", "this week", "next month", "last year",
        "from 9am to noon", "Monday to Friday", "between 2pm and 4pm on Friday", "early May", "mid 2026",
        "the 1990s", "in the 21st century", "this weekend", "tonight", "every evening", "now", "recently",
        "in the future", "every year on March 3", "Nov 19 2026 at 10:30am", "the first week of January",
    ];

    [Test]
    public void AllRecognizedTimexValuesNormalize()
    {
        var reference = new DateTime(2026, 11, 17, 10, 0, 0);
        var ctx = TestIndex.Ctx(2026, 11, 17);
        var failures = new List<string>();
        int count = 0;

        foreach (var phrase in Phrases)
        {
            foreach (var result in DateTimeRecognizer.RecognizeDateTime(phrase, Culture.English, DateTimeOptions.None, reference))
            {
                var values = (List<Dictionary<string, string>>)result.Resolution["values"];
                foreach (var v in values)
                {
                    v.TryGetValue("timex", out var timex);
                    v.TryGetValue("Mod", out var mod);
                    v.TryGetValue("type", out var type);
                    if (timex is null) continue;
                    count++;
                    try
                    {
                        TimexValue.Parse(timex, ctx, mod, type);
                        TimexValue.Parse(timex, ctx, mod); // query interpretation
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{phrase} -> {timex} (mod={mod}, type={type}): {ex.Message}");
                    }
                }
            }
        }

        TestContext.Out.WriteLine($"{count} TIMEX values checked");
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }
}
