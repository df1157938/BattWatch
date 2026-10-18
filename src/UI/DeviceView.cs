using System.Windows;
using System.Windows.Media;
using BattWatch.Core;

namespace BattWatch.UI;

/// <summary>Display-ready projection of a <see cref="DeviceBattery"/> for data templates.</summary>
public sealed class DeviceView(DeviceBattery d, int threshold)
{
    public DeviceBattery Model => d;
    public string Name => d.Name;
    public string Glyph => GlyphFor(d.Kind);
    public string Badge => d.SourceLabel;
    public int Level => d.Level;
    public string LevelText => $"{d.Level}%";
    public string Sub => $"{d.Detail} · 更新于 {d.UpdatedAt:HH:mm:ss}";
    public Visibility ChargingVisibility => d.IsCharging == true ? Visibility.Visible : Visibility.Collapsed;
    public Brush BarBrush => BrushFor(d.Level, threshold);
    public Brush BadgeBrush => d.Source switch
    {
        BatterySource.Wireless24G => Frozen(0xF3, 0xE8, 0xFD),
        BatterySource.System => Frozen(0xEE, 0xEE, 0xEE),
        _ => Frozen(0xE5, 0xF1, 0xFB),
    };
    public Brush BadgeText => d.Source switch
    {
        BatterySource.Wireless24G => Frozen(0x7A, 0x3E, 0xB1),
        BatterySource.System => Frozen(0x55, 0x55, 0x55),
        _ => Frozen(0x00, 0x5A, 0x9E),
    };

    public static string GlyphFor(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headset => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Phone => "",
        DeviceKind.Laptop => "",
        _ => "", // bluetooth
    };

    public static Brush BrushFor(int level, int threshold) =>
        level <= threshold ? Frozen(0xD1, 0x34, 0x38)
        : level <= threshold + 20 ? Frozen(0xE3, 0xA2, 0x1A)
        : Frozen(0x3B, 0xA5, 0x5C);

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
