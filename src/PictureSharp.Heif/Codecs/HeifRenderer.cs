using System.Runtime.ExceptionServices;
using PictureSharp.Formats.Heif.Container;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Codecs;

// Renders image nodes to straight-alpha RGBA: HEVC pictures, grids assembled in YCbCr, identity items, and the container
// transforms in association order. Uses no decode context, so detached nodes (gain maps) render after the load.
// Grid tiles, or the wavefront rows of a single picture, decode in parallel; the result does not depend on the thread count.
internal sealed class HeifRenderer(int maxDegreeOfParallelism, CancellationToken cancellationToken)
{
    private const int MaxDepth = 32;

    private readonly ParallelOptions parallel = new()
    {
        MaxDegreeOfParallelism = maxDegreeOfParallelism,
        CancellationToken = cancellationToken,
    };

    // alpha renders an auxiliary alpha image: its luma becomes the red channel, without colour conversion. TPixel is
    // Rgba32 (samples reduced to 8 bits) or Rgba64 (samples kept at their precision).
    public HeifCanvas<TPixel> Render<TPixel>(HeifImageNode node, bool alpha) where TPixel : unmanaged =>
        Render<TPixel>(node, null, alpha, 0);

    // Raw luma samples at the coded bit depth, without colour conversion or bit-depth reduction (depth and disparity
    // images); chroma is ignored. Grids and transforms move samples without changing their values.
    public (HeifCanvas<ushort> Samples, int BitDepth) RenderSamples(HeifImageNode node) => RenderSamples(node, 0);

    private (HeifCanvas<ushort> Samples, int BitDepth) RenderSamples(HeifImageNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw HeifReader.Error("Derived image nesting exceeds the limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        HeifCanvas<ushort> samples;
        int bitDepth;
        if (node is HeifIdentityNode identity)
        {
            (samples, bitDepth) = RenderSamples(identity.Source, depth + 1);
        }
        else
        {
            HeifPlanes planes = Assemble(node, depth);
            samples = new HeifCanvas<ushort>(planes.Width, planes.Height);
            bitDepth = planes.BitDepth;
            for (int y = 0; y < planes.Height; y++)
            {
                planes.Planes[0].AsSpan(((planes.Top + y) * planes.Strides[0]) + planes.Left, planes.Width)
                    .CopyTo(samples.Pixels.AsSpan(y * planes.Width));
            }
        }

        foreach (HeifTransform transform in node.Transforms)
        {
            samples = samples.Apply(transform, parallel);
        }

        return (samples, bitDepth);
    }

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

    private HeifCanvas<TPixel> Render<TPixel>(HeifImageNode node, HeifNclx? inheritedNclx, bool alpha, int depth) where TPixel : unmanaged
    {
        if (depth > MaxDepth)
        {
            throw HeifReader.Error("Derived image nesting exceeds the limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        HeifNclx? nclx = node.Nclx ?? inheritedNclx;
        HeifCanvas<TPixel> canvas;
        if (node is HeifIdentityNode identity)
        {
            canvas = Render<TPixel>(identity.Source, nclx, alpha, depth + 1);
        }
        else
        {
            HeifPlanes planes = Assemble(node, depth);
            canvas = new HeifCanvas<TPixel>(planes.Width, planes.Height);
            if (alpha)
            {
                if (canvas.Pixels is Rgba64[] wide)
                {
                    HeifColorConverter.ConvertAlpha(planes, wide, parallel);
                }
                else
                {
                    HeifColorConverter.ConvertAlpha(planes, (Rgba32[])(object)canvas.Pixels, parallel);
                }
            }
            else
            {
                HeifNclx colour = nclx ?? FromVui(planes.Colour);
                bool bilinear = planes.ChromaFormatIdc == 1 && HasOddCrop(node, planes.Width, planes.Height);
                var converter = new HeifColorConverter(planes.ChromaFormatIdc == 0 ? 6 : colour.Matrix, colour.FullRange);
                if (canvas.Pixels is Rgba64[] wide)
                {
                    converter.Convert(planes, wide, bilinear, parallel);
                }
                else
                {
                    converter.Convert(planes, (Rgba32[])(object)canvas.Pixels, bilinear, parallel);
                }
            }
        }

        foreach (HeifTransform transform in node.Transforms)
        {
            canvas = canvas.Apply(transform, parallel);
        }

        return canvas;
    }

    // The HEVC VUI colour description, or the HEVC defaults without one.
    private static HeifNclx FromVui(HevcColourDescription? vui) =>
        vui is { } v ? new(v.Primaries, v.Transfer, v.Matrix, v.FullRange) : HeifNclx.Unspecified;

    // A decoded HEVC picture, or a grid of them assembled in YCbCr (libheif converts the assembled image). A picture
    // outside a grid may decode its wavefront rows in parallel; grid tiles already decode in parallel.
    private HeifPlanes Assemble(HeifImageNode node, int depth, bool tile = false)
    {
        if (node is HeifCodedNode coded)
        {
            HevcFrame frame = HevcDecoder.Decode(coded.NalUnits, null, cancellationToken, tile ? null : parallel);
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

        // The first tile fixes the format; the others decode in parallel and paste into disjoint regions.
        int count = grid.Layout.Rows * grid.Layout.Columns;
        HeifPlanes first = Tile(grid, 0, depth);
        HeifPlanes planes = HeifPlanes.Create(grid.Layout.Width, grid.Layout.Height, first);
        Place(planes, first, first, 0, grid.Layout.Columns);
        var errors = new ExceptionDispatchInfo?[count];
        Parallel.For(1, count, parallel, (index, state) =>
        {
            try
            {
                Place(planes, first, Tile(grid, index, depth), index, grid.Layout.Columns);
            }
            catch (Exception exception)
            {
                // Break still runs every lower tile, so the error reported is the first in tile order, as sequentially.
                errors[index] = ExceptionDispatchInfo.Capture(exception);
                state.Break();
            }
        });

        foreach (ExceptionDispatchInfo? error in errors)
        {
            error?.Throw();
        }

        return planes;
    }

    private HeifPlanes Tile(HeifGridNode grid, int index, int depth)
    {
        HeifImageNode tileNode = grid.Tiles[index];
        if (tileNode.Transforms.Count > 0)
        {
            throw new NotSupportedException("Transformed HEIF grid tiles are not supported.");
        }

        return Assemble(tileNode, depth + 1, tile: true);
    }

    // Tiles share one size (the container checked their descriptions), so tile index determines the position.
    private static void Place(HeifPlanes planes, HeifPlanes first, HeifPlanes tile, int index, int columns)
    {
        if (!planes.HasSameFormat(tile) || (planes.ChromaFormatIdc == 1 && ((tile.Width | tile.Height) & 1) != 0)
            || tile.Width != first.Width || tile.Height != first.Height)
        {
            throw HeifReader.Error("Grid tiles have inconsistent formats.");
        }

        planes.Paste(tile, (index % columns) * tile.Width, (index / columns) * tile.Height);
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
