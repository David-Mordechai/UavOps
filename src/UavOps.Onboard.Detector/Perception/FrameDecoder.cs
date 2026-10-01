using SkiaSharp;

namespace UavOps.Onboard.Detector.Perception;

/// <summary>
/// Decodes a camera frame no larger than the detector needs: JPEG decodes natively at 1/2, 1/4 or
/// 1/8 size, far cheaper than decoding in full and shrinking after (a 1280-px frame for a 640-px
/// detector: half the decode, and the resize to the detector's square becomes a small stretch).
/// Boxes are normalized, so nothing downstream cares about the decoded size.
/// </summary>
public static class FrameDecoder
{
    public static SKBitmap? Decode(byte[] jpeg, int minWidth)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        var full = codec.Info;
        var scale = 1f;
        foreach (var candidate in new[] { 0.125f, 0.25f, 0.5f })
        {
            if (full.Width * candidate >= minWidth)
            {
                scale = candidate;
                break;
            }
        }
        var size = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return SKBitmap.Decode(jpeg);
    }
}
