using PictureSharp.Formats.Heif.Container;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Codecs;

// Renders image nodes to straight-alpha RGBA: HEVC pictures, grids assembled in YCbCr, identity items, and the container
// transforms in association order. Uses no decode context, so detached nodes (gain maps) render after the load.
internal sealed class HeifRenderer(CancellationToken cancellationToken)
{
    private const int MaxDepth = 32;

    // alpha renders an auxiliary alpha image: its luma becomes the red channel, without colour conversion.
    public HeifCanvas Render(HeifImageNode node, bool alpha) => Render(node, null, alpha, 0);

    // The colour a node is converted with, as Render finds it for its first coded picture: colr nclx (an item's own over
    // its parent's), else the HEVC VUI, else the HEVC defaults (unspecified matrix, limited range).
    public static (int Primaries, int Transfer, int Matrix, bool FullRange) ColourOf(HeifImageNode node,
        (int Primaries, int Transfer, int Matrix, bool FullRange)? inherited = null)
    {
        (int Primaries, int Transfer, int Matrix, bool FullRange)? nclx = node.Nclx ?? inherited;
        if (node.Kind == HeifImageNodeKind.Identity)
        {
            return ColourOf(node.Sources[0], nclx);
        }

        if (nclx is { } known)
        {
            return known;
        }

        // A grid takes the format of its first tile (HeifPlanes.Create); tile colr boxes are not consulted.
        HeifImageNode leaf = node;
        while (leaf.Kind == HeifImageNodeKind.Grid && leaf.Sources.Count > 0)
        {
            leaf = leaf.Sources[0];
        }

        return leaf.Kind == HeifImageNodeKind.Coded && HevcDecoder.ReadColour(leaf.NalUnits) is { } vui
            ? (vui.Primaries, vui.Transfer, vui.Matrix, vui.FullRange)
            : (2, 2, 2, false);
    }

    private HeifCanvas Render(HeifImageNode node, (int, int, int, bool)? inheritedNclx, bool alpha, int depth)
    {
        if (depth > MaxDepth)
        {
            throw HeifReader.Error("Derived image nesting exceeds the limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        (int Primaries, int Transfer, int Matrix, bool FullRange)? nclx = node.Nclx ?? inheritedNclx;
        HeifCanvas canvas;
        if (node.Kind != HeifImageNodeKind.Identity)
        {
            HeifPlanes planes = Assemble(node, depth);
            canvas = new HeifCanvas(planes.Width, planes.Height);
            if (alpha)
            {
                HeifColorConverter.ConvertAlpha(planes, canvas.Pixels);
            }
            else
            {
                // Colour comes from colr nclx, else the HEVC VUI, else the HEVC defaults (unspecified matrix, limited range).
                (int Primaries, int Transfer, int Matrix, bool FullRange) description = nclx
                    ?? (planes.Colour is { } vui ? (vui.Primaries, vui.Transfer, vui.Matrix, vui.FullRange) : (2, 2, 2, false));
                bool bilinear = planes.ChromaFormatIdc == 1 && HasOddCrop(node, planes.Width, planes.Height);
                var converter = new HeifColorConverter(planes.ChromaFormatIdc == 0 ? 6 : description.Matrix, description.FullRange);
                converter.Convert(planes, canvas.Pixels, bilinear, cancellationToken);
            }
        }
        else
        {
            canvas = Render(node.Sources[0], nclx, alpha, depth + 1);
        }

        foreach (HeifBox transform in node.Transforms)
        {
            canvas = transform.Type switch
            {
                0x636c6170 => canvas.Crop(transform.Data.Span),
                0x69726f74 => canvas.Rotate(transform.Data.Span[0] & 3),
                _ => canvas.Mirror(topBottom: (transform.Data.Span[0] & 1) == 0),
            };
        }

        return canvas;
    }

    // A decoded HEVC picture, or a grid of them assembled in YCbCr (libheif converts the assembled image).
    private HeifPlanes Assemble(HeifImageNode node, int depth)
    {
        if (node.Kind == HeifImageNodeKind.Coded)
        {
            HevcFrame frame = HevcDecoder.Decode(node.NalUnits, null, cancellationToken);
            if (frame.CroppedWidth != node.StoredSize.Width || frame.CroppedHeight != node.StoredSize.Height)
            {
                throw HeifReader.Error("HEVC picture size does not match the image item's spatial extents.");
            }

            return HeifPlanes.FromFrame(frame);
        }

        if (node.Kind != HeifImageNodeKind.Grid || depth > MaxDepth)
        {
            throw new NotSupportedException("HEIF grids must consist of HEVC or grid image items.");
        }

        (int rows, int columns, int gridWidth, int gridHeight) = node.Grid;
        HeifPlanes? grid = null;
        int index = 0;
        int y = 0;
        for (int row = 0; row < rows; row++)
        {
            int x = 0;
            int tileHeight = 0;
            for (int column = 0; column < columns; column++)
            {
                HeifImageNode tileNode = node.Sources[index++];
                if (tileNode.Transforms.Count > 0)
                {
                    throw new NotSupportedException("Transformed HEIF grid tiles are not supported.");
                }

                HeifPlanes tile = Assemble(tileNode, depth + 1);
                grid ??= HeifPlanes.Create(gridWidth, gridHeight, tile);
                if (!grid.HasSameFormat(tile) || (grid.ChromaFormatIdc == 1 && ((tile.Width | tile.Height) & 1) != 0))
                {
                    throw HeifReader.Error("Grid tiles have inconsistent formats.");
                }

                grid.Paste(tile, x, y);
                x += tile.Width;
                tileHeight = tile.Height;
            }

            y += tileHeight;
        }

        return grid!;
    }

    // libheif crops 4:2:0 images at an odd column or row by upsampling chroma to 4:4:4 (bilinear) first.
    private static bool HasOddCrop(HeifImageNode node, int width, int height)
    {
        foreach (HeifBox transform in node.Transforms)
        {
            ReadOnlySpan<byte> data = transform.Data.Span;
            if (transform.Type == 0x69726f74 && (data[0] & 1) != 0)
            {
                (width, height) = (height, width);
            }
            else if (transform.Type == 0x636c6170)
            {
                (int left, int top, int croppedWidth, int croppedHeight) = HeifCanvas.CleanAperture(data, width, height);
                if (((left | top) & 1) != 0)
                {
                    return true;
                }

                (width, height) = (croppedWidth, croppedHeight);
            }
        }

        return false;
    }
}
