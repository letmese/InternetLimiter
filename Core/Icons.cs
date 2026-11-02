using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Valve.Core;

/// <summary>
/// Pulls the real executable icon for a process, the way NetLimiter shows one per
/// row. Cached by path because the tree re-renders constantly.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static ImageSource? _connIcon;

    public static ImageSource ConnectionIcon
    {
        get
        {
            if (_connIcon is not null)
            {
                return _connIcon;
            }

            var group = new DrawingGroup();
            using (DrawingContext ctx = group.Open())
            {
                var brush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                brush.Freeze();
                var pen = new Pen(brush, 1.2);
                pen.Freeze();

                var geometry = new StreamGeometry();
                using (StreamGeometryContext gc = geometry.Open())
                {
                    gc.BeginFigure(new Point(2, 8), true, true);
                    gc.LineTo(new Point(8, 8), true, false);
                    gc.LineTo(new Point(8, 4), true, false);
                    gc.LineTo(new Point(14, 8), true, false);
                    gc.LineTo(new Point(8, 12), true, false);
                    gc.LineTo(new Point(8, 8), true, false);
                }
                geometry.Freeze();
                ctx.DrawGeometry(brush, pen, geometry);
            }
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            _connIcon = img;
            return _connIcon;
        }
    }

    public static ImageSource? ForPath(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(exePath, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource? image = null;
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
            }
        }
        catch (Exception)
        {
            // A protected or missing binary simply gets no icon.
        }

        lock (Gate)
        {
            Cache[exePath] = image;
        }

        return image;
    }
}
