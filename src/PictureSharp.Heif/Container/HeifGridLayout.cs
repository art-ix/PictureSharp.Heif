namespace PictureSharp.Formats.Heif.Container;

// ISO/IEC 23008-12 6.6.2.3 ImageGrid: tiles in rows and columns, cropped to the output size.
internal readonly record struct HeifGridLayout(int Rows, int Columns, int Width, int Height);
