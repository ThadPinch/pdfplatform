namespace FrontEndSuite.PdfPlatform.Cos;

/// <summary>Base type for every object in the PDF COS object model.</summary>
public abstract class CosObject
{
    /// <summary>Set when this object is the target of an indirect reference in a parsed document.</summary>
    public CosIndirectReference? IndirectReference { get; internal set; }

    /// <summary>Follows indirect references to the underlying object. Direct objects return themselves.</summary>
    public virtual CosObject Resolve() => this;

    public bool IsNull => this is CosNull;
}

public sealed class CosNull : CosObject
{
    public static readonly CosNull Instance = new();

    private CosNull()
    {
    }

    public override string ToString() => "null";
}

public sealed class CosBoolean : CosObject
{
    public static readonly CosBoolean True = new(true);
    public static readonly CosBoolean False = new(false);

    private CosBoolean(bool value)
    {
        Value = value;
    }

    public bool Value { get; }

    public static CosBoolean Of(bool value) => value ? True : False;

    public override string ToString() => Value ? "true" : "false";
}

public sealed class CosNumber : CosObject
{
    public CosNumber(long value)
    {
        DoubleValue = value;
        IsInteger = true;
    }

    public CosNumber(int value)
        : this((long)value)
    {
    }

    public CosNumber(double value)
    {
        DoubleValue = value;
        IsInteger = false;
    }

    public double DoubleValue { get; }
    public bool IsInteger { get; }
    public int IntValue => (int)DoubleValue;
    public long LongValue => (long)DoubleValue;
    public float FloatValue => (float)DoubleValue;

    public override string ToString() => IsInteger
        ? LongValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : DoubleValue.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
