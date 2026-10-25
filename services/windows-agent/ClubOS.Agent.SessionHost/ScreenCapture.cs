using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ClubOS.Agent.Core;
using ClubOS.Contracts;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Снимок экрана для удалённого доступа (D-022): весь виртуальный экран (все мониторы), уменьшенный до
/// <see cref="RemoteLimits.ScreenshotMaxWidth"/> по ширине, JPEG. Если не влезает в лимит канала — качество ниже.
/// </summary>
internal static class ScreenCapture
{
    private static readonly long[] Qualities = [60, 45, 30];

    public static HostMessage Capture(string requestId)
    {
        var bounds = SystemInformation.VirtualScreen;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var scale = Math.Min(1.0, (double)RemoteLimits.ScreenshotMaxWidth / bounds.Width);
        var width = Math.Max(1, (int)(bounds.Width * scale));
        var height = Math.Max(1, (int)(bounds.Height * scale));
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, width, height);
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        foreach (var quality in Qualities)
        {
            using var stream = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            small.Save(stream, codec, parameters);
            var data = Convert.ToBase64String(stream.ToArray());
            // Запас на JSON-обёртку результата.
            if (data.Length < RemoteLimits.MaxOutputChars - 4096)
            {
                return new HostMessage { Id = requestId, Ok = true, Data = data, Width = width, Height = height };
            }
        }

        return new HostMessage { Id = requestId, Ok = false, Error = "Снимок экрана слишком большой для передачи." };
    }
}
