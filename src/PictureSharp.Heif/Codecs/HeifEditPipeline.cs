namespace PictureSharp.Formats.Heif.Codecs;

// The fixed order of HeifEdits: Photos sliders first, then the portrait blur on the adjusted pixels.
internal static class HeifEditPipeline
{
    private static readonly HeifPhotosAdjustments NoAdjustments = new();

    public static Image Apply(Image photo, HeifEdits edits, HeifPortraitMaps? maps, bool fromFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(edits);
        if (photo.Frames.Count != 1)
        {
            throw new ArgumentException("The photograph must have exactly one frame.", nameof(photo));
        }

        // The Photos sliders and the portrait blur work on 8-bit samples.
        if (photo.PixelType != PixelType.Rgba32)
        {
            throw new NotSupportedException("HEIF edits render 8-bit images only; convert the photograph with Clone(x => x.ConvertPixelType(PixelType.Rgba32)) first.");
        }

        HeifPortraitRenderer? blur = null;
        if (edits.Portrait is { } portrait)
        {
            if (maps is null)
            {
                throw new ArgumentException("A portrait effect needs the photograph's depth (HeifPortraitMaps).", nameof(maps));
            }

            blur = HeifPortraitSetup.Create(photo, maps, portrait, edits.MaxDegreeOfParallelism, fromFile, cancellationToken);
        }

        // Without any stage the sliders' identity still returns a copy, as rendering empty adjustments always did.
        bool adjust = edits.Adjustments is { IsEmpty: false } || blur is null;
        long pixels = (long)photo.Width * photo.Height;
        long working = blur is null ? pixels : blur.WorkingPixels() + (adjust ? pixels : 0);
        if (working > edits.MaxPixels)
        {
            throw new ImageFormatException($"Rendering the edits needs {working} working pixels, which exceeds MaxPixels ({edits.MaxPixels}).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Image? adjusted = null;
        try
        {
            if (adjust)
            {
                var sliders = new HeifPhotosRenderer(photo.Width, photo.Height, edits.Adjustments ?? NoAdjustments, edits.MaxDegreeOfParallelism,
                    cancellationToken);
                adjusted = photo.Clone(x => x.Apply(frame => sliders.Render(photo.GetPixelMemory<Rgba32>(), frame.GetPixelMemory<Rgba32>())), cancellationToken);
                if (blur is null)
                {
                    Image result = adjusted;
                    adjusted = null;
                    return result;
                }
            }

            Image source = adjusted ?? photo;
            return source.Clone(x => x.Apply(frame => blur!.Render(source.GetPixelMemory<Rgba32>(), frame.GetPixelMemory<Rgba32>())), cancellationToken);
        }
        finally
        {
            adjusted?.Dispose();
        }
    }
}
