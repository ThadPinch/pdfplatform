namespace FrontEndSuite.PdfPlatform.Cos;

/// <summary>Resolves indirect references to their target objects. Implemented by the file parser.</summary>
public interface ICosResolver
{
    CosObject ResolveReference(CosIndirectReference reference);
}

public sealed class CosIndirectReference : CosObject, IEquatable<CosIndirectReference>
{
    public CosIndirectReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public int ObjectNumber { get; }

    public int Generation { get; }

    public ICosResolver? Resolver { get; init; }

    public override CosObject Resolve()
    {
        var target = Resolver?.ResolveReference(this) ?? CosNull.Instance;
        var guard = 0;
        while (target is CosIndirectReference nested && guard++ < 32)
        {
            target = nested.Resolver?.ResolveReference(nested) ?? CosNull.Instance;
        }

        return target is CosIndirectReference ? CosNull.Instance : target;
    }

    public bool Equals(CosIndirectReference? other) =>
        other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation;

    public override bool Equals(object? obj) => obj is CosIndirectReference other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);

    public override string ToString() => $"{ObjectNumber} {Generation} R";
}
