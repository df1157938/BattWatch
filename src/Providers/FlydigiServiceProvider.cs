using System.IO;
using System.IO.Pipes;
using System.Text;
using BattWatch.Core;

namespace BattWatch.Providers;

/// <summary>
/// Flydigi (飞智) gamepads in XInput mode. There the pad enumerates as an Xbox 360 controller
/// (045E:028E) and its vendor channel is only reachable through Space Station's kernel filter
/// driver, whose communication port takes a single, administrator-only connection that
/// SpaceStationService.exe already holds. So the reading is taken from that service instead,
/// over the same local pipe its own UI uses (Space Station 4.2+ must be installed).
///
/// Pipe \\.\pipe\fcs.sock, open to BUILTIN\Users, one client at a time. Frames in both directions:
///   "FDG_PROTOCOL\n" + int32 LE length + protobuf payload
/// Request  IpcCommand { cmd_id (1) = 4097 GetConnectedDevices }: read-only, answered from memory.
/// Reply    IpcResult  { cmd_id (1) = 4097, get_connected_devices (5) { repeated DeviceInfo (1) } }
/// DeviceInfo: product_name 1, category 2 (1 = controller), connect_type 3 (1 wired, 2 dongle,
///   3 bluetooth), device_code 4 ("f4" = Vader 4 Pro), device_mac 6, is_connected 8, battery 9.
/// battery is 0 = not read yet, 1…5 = bars, 6 = charging (level not reported).
///
/// The service reads the battery when the pad connects, not periodically, so the value is as of
/// the pad's last power-on. While Space Station's window is open it owns the pipe and the previous
/// reading is kept.
///
/// Layout taken from the service's own assemblies (FlydigiWorker, CommunicationProtocolHandler,
/// Flydigi.SharedResources protobuf classes, HeartBeatCommandFactory).
/// </summary>
public sealed class FlydigiServiceProvider : IBatteryProvider
{
    const string PipeName = "fcs.sock";
    const int GetConnectedDevices = 4097;
    const int CategoryController = 1;
    const int BatteryCharging = 6;
    static readonly byte[] Header = Encoding.ASCII.GetBytes("FDG_PROTOCOL\n");

    static readonly TimeSpan KeepWhileBusy = TimeSpan.FromMinutes(30);

    readonly Dictionary<string, DeviceBattery> _last = new();
    DateTime _lastOk = DateTime.MinValue;

    public string Name => "Flydigi Space Station";

    public async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        List<Device>? devices;
        try
        {
            devices = await QueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (MchoseHidProvider.Verbose) Log.Write($"  flydigi service: {ex.GetType().Name}: {ex.Message}");
            devices = null;
        }
        // Pipe busy (Space Station's window is open) or service gone: keep what we had for a while.
        if (devices is null)
            return DateTime.Now - _lastOk < KeepWhileBusy ? _last.Values.ToList() : [];
        _lastOk = DateTime.Now;

        var result = new List<DeviceBattery>();
        foreach (var d in devices)
        {
            if (MchoseHidProvider.Verbose)
                Log.Write($"  flydigi service device \"{d.ProductName}\" code={d.DeviceCode} cat={d.Category} conn={d.ConnectType} connected={d.IsConnected} battery={d.Battery} mac={d.Mac}");
            if (d.Category != CategoryController || !d.IsConnected) continue;

            string id = FlydigiHidProvider.DeviceId(d.Mac);
            bool charging = d.Battery == BatteryCharging;
            int? level = d.Battery is >= 1 and <= 5 ? d.Battery * 20
                : charging && _last.TryGetValue(id, out var prev) ? prev.Level
                : null;
            if (level is null) continue; // not read yet, or charging with no earlier reading

            bool wired = d.ConnectType == 1;
            result.Add(new DeviceBattery
            {
                Id = id,
                Name = d.DeviceCode == "f4" ? FlydigiHidProvider.Vader4ProName
                    : string.IsNullOrEmpty(d.ProductName) ? "飞智手柄" : d.ProductName,
                Kind = DeviceKind.Gamepad,
                Source = wired ? BatterySource.Usb : d.ConnectType == 3 ? BatterySource.Bluetooth : BatterySource.Wireless24G,
                Level = level.Value,
                IsCharging = charging,
                Detail = "飞智空间站服务 · 5 格",
                UpdatedAt = DateTime.Now,
            });
        }
        _last.Clear();
        foreach (var d in result) _last[d.Id] = d;
        return result;
    }

    static async Task<List<Device>?> QueryAsync(CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(300, ct);
        }
        catch (TimeoutException)
        {
            if (MchoseHidProvider.Verbose) Log.Write("  flydigi service: pipe missing or busy");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        var request = new List<byte>(Header);
        byte[] payload = [0x08, .. Varint(GetConnectedDevices)];
        request.AddRange(BitConverter.GetBytes(payload.Length));
        request.AddRange(payload);
        await pipe.WriteAsync(request.ToArray(), timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        // The service may push unrelated notifications on the same pipe; skip them.
        for (int i = 0; i < 20; i++)
        {
            var frame = await ReadFrameAsync(pipe, timeout.Token);
            if (frame is null) return null;
            var devices = ParseResult(frame);
            if (devices is not null) return devices;
        }
        return null;
    }

    static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        var header = new StringBuilder();
        while (true)
        {
            if (await s.ReadAsync(one, ct) == 0) return null;
            if (one[0] == (byte)'\n') break;
            if (header.Length >= 256) return null;
            header.Append((char)one[0]);
        }
        if (header.ToString().Trim() != "FDG_PROTOCOL") return null; // chunked/compressed: never used for this reply

        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        int n = BitConverter.ToInt32(len);
        if (n is <= 0 or > 1 << 20) return null;
        var data = new byte[n];
        await s.ReadExactlyAsync(data, ct);
        return data;
    }

    // ---------- minimal protobuf reading ----------

    internal sealed record Device(string ProductName, int Category, int ConnectType, string DeviceCode,
        string Mac, bool IsConnected, int Battery);

    /// <summary>Devices of a GetConnectedDevices IpcResult, or null when the frame is something else.</summary>
    internal static List<Device>? ParseResult(ReadOnlySpan<byte> msg)
    {
        int cmd = 0;
        List<Device>? devices = null;
        foreach (var f in Fields(msg.ToArray()))
        {
            if (f.Number == 1 && f.Bytes is null) cmd = (int)f.Value;
            else if (f.Number == 5 && f.Bytes is not null)
            {
                devices = [];
                foreach (var d in Fields(f.Bytes))
                    if (d.Number == 1 && d.Bytes is not null) devices.Add(ParseDevice(d.Bytes));
            }
        }
        if (cmd != GetConnectedDevices) return null;
        return devices ?? [];
    }

    static Device ParseDevice(byte[] msg)
    {
        string name = "", code = "", mac = "";
        int category = 0, connect = 0, battery = 0;
        bool connected = false;
        foreach (var f in Fields(msg))
        {
            switch (f.Number)
            {
                case 1 when f.Bytes is not null: name = Encoding.UTF8.GetString(f.Bytes); break;
                case 2: category = (int)f.Value; break;
                case 3: connect = (int)f.Value; break;
                case 4 when f.Bytes is not null: code = Encoding.UTF8.GetString(f.Bytes); break;
                case 6 when f.Bytes is not null: mac = Encoding.UTF8.GetString(f.Bytes); break;
                case 8: connected = f.Value != 0; break;
                case 9: battery = (int)f.Value; break;
            }
        }
        return new Device(name, category, connect, code, mac, connected, battery);
    }

    readonly record struct Field(int Number, ulong Value, byte[]? Bytes);

    static List<Field> Fields(byte[] msg)
    {
        var list = new List<Field>();
        int pos = 0;
        while (pos < msg.Length)
        {
            ulong tag = ReadVarint(msg, ref pos);
            int number = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0: list.Add(new Field(number, ReadVarint(msg, ref pos), null)); break;
                case 1: pos += 8; break;
                case 5: pos += 4; break;
                case 2:
                    int len = (int)ReadVarint(msg, ref pos);
                    if (len < 0 || pos + len > msg.Length) throw new InvalidDataException("protobuf length");
                    list.Add(new Field(number, 0, msg[pos..(pos + len)]));
                    pos += len;
                    break;
                default: throw new InvalidDataException("protobuf wire type");
            }
        }
        return list;
    }

    static ulong ReadVarint(byte[] msg, ref int pos)
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (pos >= msg.Length) throw new InvalidDataException("protobuf varint");
            byte b = msg[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
        }
        throw new InvalidDataException("protobuf varint");
    }

    internal static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            bytes.Add(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
        return bytes.ToArray();
    }
}
