namespace BattWatch.Core;

public enum DeviceKind { Unknown, Mouse, Keyboard, Headset, Gamepad, Phone, Laptop }

/// <summary>Where a reading came from; shown as a badge in the UI.</summary>
public enum BatterySource { Bluetooth, BluetoothLE, Wireless24G, System }

public sealed record DeviceBattery
{
    /// <summary>Stable key used to merge readings and store history.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public BatterySource Source { get; init; }
    public int Level { get; init; }
    public bool? IsCharging { get; init; }
    /// <summary>How the value was obtained, e.g. "GATT 0x2A19".</summary>
    public string Detail { get; init; } = "";
    public DateTime UpdatedAt { get; init; } = DateTime.Now;

    public string SourceLabel => Source switch
    {
        BatterySource.Bluetooth => "蓝牙",
        BatterySource.BluetoothLE => "BLE",
        BatterySource.Wireless24G => "2.4G",
        _ => "本机",
    };
}

public interface IBatteryProvider
{
    string Name { get; }
    Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct);
}

public static class DeviceKindGuess
{
    public static DeviceKind FromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("鼠标")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("键盘") || n.Contains("kb")) return DeviceKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("手柄") || n.Contains("xbox")) return DeviceKind.Gamepad;
        if (n.Contains("bud") || n.Contains("tws") || n.Contains("headset") || n.Contains("headphone")
            || n.Contains("耳机") || n.Contains("airpods") || n.Contains("earbuds")) return DeviceKind.Headset;
        return DeviceKind.Unknown;
    }

    /// <summary>Bluetooth class-of-device major class → kind.</summary>
    public static DeviceKind FromClassOfDevice(uint cod)
    {
        uint major = (cod >> 8) & 0x1F, minor = (cod >> 2) & 0x3F;
        return major switch
        {
            0x02 => DeviceKind.Phone,
            0x04 => DeviceKind.Headset,
            0x05 => (minor & 0x30) switch
            {
                0x10 => DeviceKind.Keyboard,
                0x20 => DeviceKind.Mouse,
                0x30 => DeviceKind.Keyboard,
                _ => (minor & 0x0F) is 0x01 or 0x02 ? DeviceKind.Gamepad : DeviceKind.Unknown,
            },
            _ => DeviceKind.Unknown,
        };
    }

    /// <summary>BLE GAP appearance → kind.</summary>
    public static DeviceKind FromAppearance(ushort appearance) => (appearance >> 6) switch
    {
        0x0F => (appearance & 0x3F) switch
        {
            1 => DeviceKind.Keyboard,
            2 => DeviceKind.Mouse,
            3 or 4 => DeviceKind.Gamepad,
            _ => DeviceKind.Unknown,
        },
        0x01 => DeviceKind.Phone,
        0x25 => DeviceKind.Headset,
        _ => DeviceKind.Unknown,
    };
}
