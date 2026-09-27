using SkiaSharp;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector;

/// <summary>
/// Cuts a close-up around a candidate out of a frame and scales it up, so the model gets many
/// image patches for an object that was only a few in the whole frame. The crop is square, twice
/// the box's longer side (at least <see cref="MinSidePixels"/>), keeping some surroundings for scale.
/// </summary>
public static class FrameCropper
{
    public const int OutputPixels = 384;
    public const int MinSidePixels = 80;

    /// <param name="metersAcross">How much ground the crop covers, for the model's sense of size.</param>
    public static byte[] Crop(byte[] jpeg, BoundingBox box, double metersPerPixel, out double metersAcross)
    {
        using var image = SKImage.FromEncodedData(jpeg) ?? throw new InvalidDataException("The frame isn't a readable image.");
        var cx = box.CenterX / 1000 * image.Width;
        var cy = box.CenterY / 1000 * image.Height;
        var boxSide = Math.Max((box.X2 - box.X1) / 1000 * image.Width, (box.Y2 - box.Y1) / 1000 * image.Height);
        var side = Math.Max(boxSide * 2, MinSidePixels);
        metersAcross = side * metersPerPixel;

        using var surface = SKSurface.Create(new SKImageInfo(OutputPixels, OutputPixels))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        surface.Canvas.Clear(SKColors.Black);
        var source = SKRect.Create((float)(cx - side / 2), (float)(cy - side / 2), (float)side, (float)side);
        surface.Canvas.DrawImage(image, source, new SKRect(0, 0, OutputPixels, OutputPixels), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
