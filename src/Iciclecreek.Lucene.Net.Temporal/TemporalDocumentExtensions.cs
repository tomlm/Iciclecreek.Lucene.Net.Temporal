using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>Shortcuts over the strategies, in the style of Lucene.NET's <c>doc.AddStringField(...)</c>.</summary>
public static class TemporalDocumentExtensions
{
    /// <summary>Adds a concrete date value filterable with whole or partial TIMEX queries.</summary>
    public static Document AddTemporalDateField(this Document document, TemporalDateStrategy strategy, DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(strategy);
        foreach (var field in strategy.CreateIndexableFields(value, zone))
            document.Add(field);
        return document;
    }

    /// <summary>
    /// Adds TIMEX references to a document. May be called more than once for the same field, but every
    /// call must use the same anchor (the document's sent/created time).
    /// </summary>
    public static Document AddTimexField(this Document document, TimexStrategy strategy, IEnumerable<TimexReference> references, TemporalContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(context);

        bool includeAnchor = true;
        if (document.GetField(strategy.AnchorFieldName) is IIndexableField existing)
        {
            if (existing.GetInt64Value() != context.AnchorLocal.Ticks)
                throw new InvalidOperationException(
                    $"Field '{strategy.FieldName}' already has a different anchor on this document; all references in a field share the document's anchor.");
            includeAnchor = false;
        }

        foreach (var field in strategy.CreateIndexableFields(references, context, includeAnchor))
            document.Add(field);
        return document;
    }

    /// <summary>Adds one TIMEX reference to a document.</summary>
    public static Document AddTimexField(this Document document, TimexStrategy strategy, string timex, TemporalContext context, string? mod = null, string? type = null) =>
        document.AddTimexField(strategy, [new TimexReference(timex, mod, type)], context);
}
