using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace Iciclecreek.Lucene.Net.Temporal;

/// <summary>Emits a precomputed list of signature tokens.</summary>
internal sealed class SignatureTokenStream : TokenStream
{
    private readonly string[] _tokens;
    private readonly ICharTermAttribute _term;
    private int _index;

    public SignatureTokenStream(IEnumerable<string> tokens)
    {
        _tokens = tokens.Distinct(StringComparer.Ordinal).ToArray();
        _term = AddAttribute<ICharTermAttribute>();
    }

    public override bool IncrementToken()
    {
        ClearAttributes();
        if (_index >= _tokens.Length)
            return false;
        _term.SetEmpty().Append(_tokens[_index++]);
        return true;
    }

    public override void Reset()
    {
        base.Reset();
        _index = 0;
    }
}

internal static class SignatureField
{
    /// <summary>Indexed, tokenized (required for a TokenStream field), not stored, docs-only, no norms.</summary>
    public static readonly FieldType Type = CreateType();

    private static FieldType CreateType()
    {
        var type = new FieldType
        {
            IsIndexed = true,
            IsTokenized = true,
            IsStored = false,
            OmitNorms = true,
            IndexOptions = IndexOptions.DOCS_ONLY,
        };
        type.Freeze();
        return type;
    }

    public static Field Create(string name, IEnumerable<string> tokens) =>
        new(name, new SignatureTokenStream(tokens), Type);
}
