namespace PictureSharp.Formats.Heif.Container;

// colr nclx: ITU-T H.273 colour primaries, transfer characteristics, matrix coefficients and the full-range flag.
internal readonly record struct HeifNclx(int Primaries, int Transfer, int Matrix, bool FullRange)
{
    // HEVC's defaults without a colour description: unspecified, limited range.
    public static HeifNclx Unspecified => new(2, 2, 2, false);
}
