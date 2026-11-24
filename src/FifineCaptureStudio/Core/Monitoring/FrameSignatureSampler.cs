using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FifineCaptureStudio.Core.Monitoring;

/// <summary>
/// Produces the tiny 64×36 BGRA sample the cadence estimator compares. The decoded bitmap is already
/// frozen, so this can run on the capture thread; only ~9 KB is copied instead of the full frame.
/// </summary>
public static class FrameSignatureSampler
{
    public static bool TrySample(BitmapSource source, byte[] destination, out int width, out int height)
    {
        width = VisualCadenceEstimator.Width;
        height = VisualCadenceEstimator.Height;
        try
        {
            BitmapSource scaled = source;
            if (source.PixelWidth != width || source.PixelHeight != height)
            {
                var transform = new TransformedBitmap();
                transform.BeginInit();
                transform.Source = source;
                transform.Transform = new ScaleTransform(width / (double)source.PixelWidth,
                    height / (double)source.PixelHeight);
                transform.EndInit();
                transform.Freeze();
                scaled = transform;
            }

            if (scaled.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                scaled = converted;
            }

            scaled.CopyPixels(destination, width * 4, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
