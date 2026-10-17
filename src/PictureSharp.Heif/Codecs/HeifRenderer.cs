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
    public static HeifNclx ColourOf(HeifImageNode node, HeifNclx? inherited = null)
    {
        HeifNclx? nclx = node.Nclx ?? inherited;
        if (node is HeifIdentityNode identity)
        {
            return ColourOf(identity.Source, nclx);
        }

        if (nclx is { } known)
        {
            return known;
        }

        // A grid takes the format of its first tile (HeifPlanes.Create); tile colr boxes are not consulted.
        HeifImageNode leaf = node;
        while (leaf is HeifGridNode { Tiles: [var first, ..] })
        {
            leaf = first;
        }

        return leaf is HeifCodedNode coded ? FromVui(HevcDecoder.ReadColour(coded.NalUnits)) : HeifNclx.Unspecified;
    }

    private HeifCanvas Render(HeifImageNode node, HeifNclx? inheritedNclx, bool alpha, int depth)
    {
        if (depth > MaxDepth)
        {
            throw HeifReader.Error("Derived image nesting exceeds the limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        HeifNclx? nclx = node.Nclx ?? inheritedNclx;
        HeifCanvas canvas;
        if (node is HeifIdentityNode identity)
        {
            canvas = Render(identity.Source, nclx, alpha, depth + 1);
        }
        else
        {
            HeifPlanes planes = Assemble(node, depth);
            canvas = new HeifCanvas(planes.Width, planes.Height);
            if (alpha)
            {
                HeifColorConverter.ConvertAlpha(planes, canvas.Pixels);
            }
            else
            {
                HeifNclx colour = nclx ?? FromVui(planes.Colour);
                bool bilinear = planes.ChromaFormatIdc == 1 && HasOddCrop(node, planes.Width, planes.Height);
                var converter = new HeifColorConverter(planes.ChromaFormatIdc == 0 ? 6 : colour.Matrix, colour.FullRange);
                converter.Convert(planes, canvas.Pixels, bilinear, cancellationToken);
            }
        }

        foreach (HeifTransform transform in node.Transforms)
        {
            canvas = canvas.Apply(transform);
        }

        return canvas;
    }

    // The HEVC VUI colour description, or the HEVC defaults without one.
    private static HeifNclx FromVui(HevcColourDescription? vui) =>
        vui is { } v ? new(v.Primaries, v.Transfer, v.Matrix, v.FullRange) : HeifNclx.Unspecified;

    // A decoded HEVC picture, or a grid of them assembled in YCbCr (libheif converts the assembled image).
    private HeifPlanes Assemble(HeifImageNode node, int depth)
    {
        if (node is HeifCodedNode coded)
        {
            HevcFrame frame = HevcDecoder.Decode(coded.NalUnits, null, cancellationToken);
            if ((frame.CroppedWidth, frame.CroppedHeight) != coded.StoredSize)
            {
                throw HeifReader.Error("HEVC picture size does not match the image item's spatial extents.");
            }

            return HeifPlanes.FromFrame(frame);
        }

        if (node is not HeifGridNode grid || depth > MaxDepth)
        {
            throw new NotSupportedException("HEIF grids must consist of HEVC or grid image items.");
        }

        HeifPlanes? planes = null;
        int index = 0;
        int y = 0;
        for (int row = 0; row < grid.Layout.Rows; row++)
        {
            int x = 0;
            int tileHeight = 0;
            for (int column = 0; column < grid.Layout.Columns; column++)
            {
                HeifImageNode tileNode = grid.Tiles[index++];
                if (tileNode.Transforms.Count > 0)
                {
                    throw new NotSupportedException("Transformed HEIF grid tiles are not supported.");
                }

                HeifPlanes tile = Assemble(tileNode, depth + 1);
                planes ??= HeifPlanes.Create(grid.Layout.Width, grid.Layout.Height, tile);
                if (!planes.HasSameFormat(tile) || (planes.ChromaFormatIdc == 1 && ((tile.Width | tile.Height) & 1) != 0))
                {
                    throw HeifReader.Error("Grid tiles have inconsistent formats.");
                }

                planes.Paste(tile, x, y);
                x += tile.Width;
                tileHeight = tile.Height;
            }

            y += tileHeight;
        }

        return planes!;
    }

    // libheif crops 4:2:0 images at an odd column or row by upsampling chroma to 4:4:4 (bilinear) first.
    private static bool HasOddCrop(HeifImageNode node, int width, int height)
    {
        foreach (HeifTransform transform in node.Transforms)
        {
            switch (transform)
            {
                case HeifRotation { SwapsDimensions: true }:
                    (width, height) = (height, width);
                    break;
                case HeifCleanAperture clap:
                    (int left, int top, int croppedWidth, int croppedHeight) = clap.Window(width, height);
                    if (((left | top) & 1) != 0)
                    {
                        return true;
                    }

                    (width, height) = (croppedWidth, croppedHeight);
                    break;
            }
        }

        return false;
    }
}
