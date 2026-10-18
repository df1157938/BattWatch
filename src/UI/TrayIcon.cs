using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BattWatch.Core;

namespace BattWatch.UI;

/// <summary>Tray icon whose image shows the lowest battery level among visible devices.</summary>
sealed class TrayIcon : IDisposable
{
    readonly App _app;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _widgetItem;
    IntPtr _hIcon;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _app.ShowMain());
        menu.Items.Add("立即刷新", null, async (_, _) => await _app.Monitor.RefreshAsync());
        _widgetItem = new ToolStripMenuItem("桌面悬浮窗", null, (_, _) => _app.SetWidgetVisible(!_app.WidgetVisible));
        menu.Items.Add(_widgetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _app.Quit());
        menu.Opening += (_, _) => _widgetItem.Checked = _app.WidgetVisible;

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "电量哨兵 · 正在读取…" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _app.ShowMain(); };
        SetIcon(null, false);
    }

    public void Update(List<DeviceBattery> devices)
    {
        var lowest = devices.Where(d => d.Kind != DeviceKind.Laptop).MinBy(d => d.Level)
                     ?? devices.MinBy(d => d.Level);
        SetIcon(lowest?.Level, lowest is not null && lowest.Level <= _app.Settings.LowThreshold);

        // NotifyIcon.Text is capped at 127 chars.
        var text = devices.Count == 0
            ? "电量哨兵 · 未发现设备"
            : string.Join("\n", devices.Select(d => $"{Shorten(d.Name)} {d.Level}%{(d.IsCharging == true ? "⚡" : "")}"));
        _icon.Text = text.Length > 127 ? text[..124] + "…" : text;
    }

    public void NotifyLow(DeviceBattery d, int threshold) =>
        _icon.ShowBalloonTip(8000, "电量不足", $"{d.Name} 仅剩 {d.Level}%（阈值 {threshold}%），记得充电。", ToolTipIcon.Warning);

    static string Shorten(string s) => s.Length > 18 ? s[..17] + "…" : s;

    void SetIcon(int? level, bool low)
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var fill = level is null ? Color.FromArgb(120, 120, 120)
                : low ? Color.FromArgb(209, 52, 56)
                : level <= _app.Settings.LowThreshold + 20 ? Color.FromArgb(227, 162, 26)
                : Color.FromArgb(59, 165, 92);
            using var bg = new SolidBrush(fill);
            using var path = RoundRect(new RectangleF(0, 3, S, S - 6), 7);
            g.FillPath(bg, path);

            var txt = level is null ? "…" : level >= 100 ? "满" : level.Value.ToString();
            float size = txt.Length >= 2 ? 15f : 19f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (txt == "满")
            {
                using var cn = new Font("Microsoft YaHei UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
                DrawCentered(g, txt, cn, S);
            }
            else DrawCentered(g, txt, font, S);
        }

        var h = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = h;
    }

    static void DrawCentered(Graphics g, string s, Font f, int size)
    {
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(s, f, Brushes.White, new RectangleF(0, 1, size, size), fmt);
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
