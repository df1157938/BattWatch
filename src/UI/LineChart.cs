using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BattWatch.Core;

namespace BattWatch.UI;

/// <summary>Minimal 0–100% time-series chart. Gaps over 30 minutes break the line.</summary>
public sealed class LineChart : FrameworkElement
{
    List<HistoryPoint> _pts = [];
    DateTime _from, _to;
    int _threshold;

    static readonly Typeface Face = new("Microsoft YaHei UI");
    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)), 1));
    static readonly Pen ThresholdPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xD1, 0x34, 0x38)), 1) { DashStyle = DashStyles.Dash });
    static readonly Pen LinePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)), 2) { LineJoin = PenLineJoin.Round });
    static readonly Brush ChargeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0xA5, 0x5C)));
    static readonly Brush Label = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    public void SetData(List<HistoryPoint> pts, DateTime from, DateTime to, int threshold)
    {
        (_pts, _from, _to, _threshold) = (pts, from, to, threshold);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 36, bottom = 22, top = 8, right = 8;
        double w = ActualWidth - left - right, h = ActualHeight - top - bottom;
        if (w <= 10 || h <= 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double X(DateTime t) => left + (t - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds) * w;
        double Y(int lvl) => top + (100 - lvl) / 100.0 * h;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var lvl in new[] { 0, 25, 50, 75, 100 })
        {
            dc.DrawLine(GridPen, new Point(left, Y(lvl)), new Point(left + w, Y(lvl)));
            var ft = new FormattedText($"{lvl}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, Label, dpi);
            dc.DrawText(ft, new Point(left - ft.Width - 6, Y(lvl) - ft.Height / 2));
        }
        dc.DrawLine(ThresholdPen, new Point(left, Y(_threshold)), new Point(left + w, Y(_threshold)));

        bool days = (_to - _from).TotalDays > 1.5;
        for (int i = 0; i <= 4; i++)
        {
            var t = _from + (_to - _from) * (i / 4.0);
            var ft = new FormattedText(t.ToString(days ? "MM-dd" : "HH:mm"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, 10, Label, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(X(t) - ft.Width / 2, left, left + w - ft.Width), top + h + 6));
        }

        if (_pts.Count == 0)
        {
            var ft = new FormattedText("这段时间没有记录", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, Label, dpi);
            dc.DrawText(ft, new Point(left + (w - ft.Width) / 2, top + (h - ft.Height) / 2));
            return;
        }

        // Shade charging stretches.
        for (int i = 0; i < _pts.Count; i++)
        {
            if (!_pts[i].Charging) continue;
            var end = i + 1 < _pts.Count ? _pts[i + 1].Time : _to;
            if (end - _pts[i].Time > TimeSpan.FromMinutes(30)) end = _pts[i].Time.AddMinutes(10);
            dc.DrawRectangle(ChargeFill, null, new Rect(new Point(X(_pts[i].Time), top), new Point(X(end), top + h)));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < _pts.Count; i++)
            {
                var p = new Point(X(_pts[i].Time), Y(_pts[i].Level));
                bool gap = i == 0 || _pts[i].Time - _pts[i - 1].Time > TimeSpan.FromMinutes(30);
                if (gap) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, LinePen, geo);
        if (_pts.Count == 1 || _pts[^1].Time - _pts[^2].Time > TimeSpan.FromMinutes(30))
            dc.DrawEllipse(LinePen.Brush, null, new Point(X(_pts[^1].Time), Y(_pts[^1].Level)), 3, 3);
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
