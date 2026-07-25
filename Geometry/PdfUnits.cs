namespace FrontEndSuite.PdfPlatform.Geometry;

public static class PdfUnits
{
    public const float PointsPerInch = 72f;

    public static float InchesToPoints(float inches) => inches * PointsPerInch;

    public static float PointsToInches(float points) => points / PointsPerInch;
}
