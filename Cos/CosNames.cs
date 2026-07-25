namespace FrontEndSuite.PdfPlatform.Cos;

/// <summary>Well-known PDF name constants. Use <c>new CosName("...")</c> for anything not listed.</summary>
public static class CosNames
{
    // Document structure
    public static readonly CosName Type = new("Type");
    public static readonly CosName Subtype = new("Subtype");
    public static readonly CosName Catalog = new("Catalog");
    public static readonly CosName Pages = new("Pages");
    public static readonly CosName Page = new("Page");
    public static readonly CosName Kids = new("Kids");
    public static readonly CosName Parent = new("Parent");
    public static readonly CosName Count = new("Count");
    public static readonly CosName Root = new("Root");
    public static readonly CosName Info = new("Info");
    public static readonly CosName Encrypt = new("Encrypt");
    public static readonly CosName Size = new("Size");
    public static readonly CosName Prev = new("Prev");
    public static readonly CosName Version = new("Version");
    public static readonly CosName Contents = new("Contents");
    public static readonly CosName Annots = new("Annots");
    public static readonly CosName Rotate = new("Rotate");

    // Cross-reference / object streams
    public static readonly CosName XRef = new("XRef");
    public static readonly CosName XRefStm = new("XRefStm");
    public static readonly CosName Index = new("Index");
    public static readonly CosName W = new("W");
    public static readonly CosName ObjStm = new("ObjStm");
    public static readonly CosName N = new("N");
    public static readonly CosName First = new("First");

    // Page boxes
    public static readonly CosName MediaBox = new("MediaBox");
    public static readonly CosName CropBox = new("CropBox");
    public static readonly CosName TrimBox = new("TrimBox");
    public static readonly CosName BleedBox = new("BleedBox");
    public static readonly CosName ArtBox = new("ArtBox");

    // Streams and filters
    public static readonly CosName Length = new("Length");
    public static readonly CosName Filter = new("Filter");
    public static readonly CosName DecodeParms = new("DecodeParms");
    public static readonly CosName DP = new("DP");
    public static readonly CosName FlateDecode = new("FlateDecode");
    public static readonly CosName DCTDecode = new("DCTDecode");
    public static readonly CosName JPXDecode = new("JPXDecode");
    public static readonly CosName CCITTFaxDecode = new("CCITTFaxDecode");
    public static readonly CosName JBIG2Decode = new("JBIG2Decode");
    public static readonly CosName RunLengthDecode = new("RunLengthDecode");
    public static readonly CosName LZWDecode = new("LZWDecode");
    public static readonly CosName ASCIIHexDecode = new("ASCIIHexDecode");
    public static readonly CosName ASCII85Decode = new("ASCII85Decode");
    public static readonly CosName Predictor = new("Predictor");
    public static readonly CosName Columns = new("Columns");
    public static readonly CosName Colors = new("Colors");
    public static readonly CosName BitsPerComponent = new("BitsPerComponent");

    // Resources / XObjects / images
    public static readonly CosName Resources = new("Resources");
    public static readonly CosName XObject = new("XObject");
    public static readonly CosName Form = new("Form");
    public static readonly CosName Image = new("Image");
    public static readonly CosName ImageMask = new("ImageMask");
    public static readonly CosName Width = new("Width");
    public static readonly CosName Height = new("Height");
    public static readonly CosName BBox = new("BBox");
    public static readonly CosName Decode = new("Decode");
    public static readonly CosName Matrix = new("Matrix");
    public static readonly CosName Properties = new("Properties");
    public static readonly CosName LW = new("LW");
    public static readonly CosName EarlyChange = new("EarlyChange");
    public static readonly CosName SMask = new("SMask");
    public static readonly CosName Mask = new("Mask");
    public static readonly CosName Intent = new("Intent");
    public static readonly CosName Thumb = new("Thumb");
    public static readonly CosName ExtGState = new("ExtGState");
    public static readonly CosName BM = new("BM");
    public static readonly CosName Compatible = new("Compatible");
    public static readonly CosName Normal = new("Normal");
    public static readonly CosName CA = new("CA");
    public static readonly CosName ca = new("ca");
    public static readonly CosName OP = new("OP");
    public static readonly CosName op = new("op");

    // Fonts
    public static readonly CosName Font = new("Font");
    public static readonly CosName BaseFont = new("BaseFont");
    public static readonly CosName FontDescriptor = new("FontDescriptor");
    public static readonly CosName FontFile = new("FontFile");
    public static readonly CosName FontFile2 = new("FontFile2");
    public static readonly CosName FontFile3 = new("FontFile3");
    public static readonly CosName DescendantFonts = new("DescendantFonts");
    public static readonly CosName ToUnicode = new("ToUnicode");
    public static readonly CosName Encoding = new("Encoding");
    public static readonly CosName Differences = new("Differences");

    // Color spaces and functions
    public static readonly CosName ColorSpace = new("ColorSpace");
    public static readonly CosName DeviceRGB = new("DeviceRGB");
    public static readonly CosName DeviceGray = new("DeviceGray");
    public static readonly CosName DeviceN = new("DeviceN");
    public static readonly CosName CalRGB = new("CalRGB");
    public static readonly CosName CalGray = new("CalGray");
    public static readonly CosName ICCBased = new("ICCBased");
    public static readonly CosName Indexed = new("Indexed");
    public static readonly CosName Separation = new("Separation");
    public static readonly CosName FunctionType = new("FunctionType");
    public static readonly CosName Domain = new("Domain");
    public static readonly CosName Range = new("Range");
    public static readonly CosName C0 = new("C0");
    public static readonly CosName C1 = new("C1");

    // Optional content
    public static readonly CosName OC = new("OC");
    public static readonly CosName OCG = new("OCG");
    public static readonly CosName OCGs = new("OCGs");
    public static readonly CosName OCProperties = new("OCProperties");
    public static readonly CosName D = new("D");
    public static readonly CosName Usage = new("Usage");
    public static readonly CosName View = new("View");
    public static readonly CosName ViewState = new("ViewState");
    public static readonly CosName Print = new("Print");
    public static readonly CosName PrintState = new("PrintState");
    public static readonly CosName Name = new("Name");
    public static readonly CosName None = new("None");

    // Annotations
    public static readonly CosName Annot = new("Annot");
    public static readonly CosName Text = new("Text");
    public static readonly CosName Rect = new("Rect");
    public static readonly CosName Note = new("Note");
    public static readonly CosName Open = new("Open");
    public static readonly CosName Subj = new("Subj");
    public static readonly CosName T = new("T");
    public static readonly CosName C = new("C");

    // Document-level metadata / output intents / forms
    public static readonly CosName Metadata = new("Metadata");
    public static readonly CosName AcroForm = new("AcroForm");
    public static readonly CosName OutputIntents = new("OutputIntents");
    public static readonly CosName OutputCondition = new("OutputCondition");
    public static readonly CosName OutputConditionIdentifier = new("OutputConditionIdentifier");
    public static readonly CosName Creator = new("Creator");
    public static readonly CosName Producer = new("Producer");
    public static readonly CosName Title = new("Title");
    public static readonly CosName CreationDate = new("CreationDate");
    public static readonly CosName ModDate = new("ModDate");
}
