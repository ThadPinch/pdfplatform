namespace FrontEndSuite.PdfPlatform.Cos;

public sealed class CosName : CosObject, IEquatable<CosName>
{
    private readonly int _hashCode;

    public CosName(string value)
    {
        Value = value;
        _hashCode = value.GetHashCode();
    }

    /// <summary>The name without the leading slash, with #xx escapes already decoded.</summary>
    public string Value { get; }

    public bool Equals(CosName? other) => other is not null && other.Value == Value;

    public override bool Equals(object? obj) => obj is CosName other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => "/" + Value;
}
