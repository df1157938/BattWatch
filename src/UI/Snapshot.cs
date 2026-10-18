using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BattWatch.UI;

static class Snapshot
{
    /// <summary>Renders a window's content (not its non-client frame) to a PNG.</summary>
    public static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (w.Background is Brush bg && w.AllowsTransparency == false)
                dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
