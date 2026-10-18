using System.Text.RegularExpressions;
using BattWatch.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BattWatch.Providers;

/// <summary>
/// Paired + connected Bluetooth devices. Two sources, merged per MAC address:
/// 1. The battery device property Windows fills in for HFP headsets and HID-over-GATT
///    devices (DEVPKEY_Bluetooth_Battery, {104EA319-…} 2) on the BTHENUM/BTHLE devnode.
/// 2. Standard GATT Battery Service (0x180F / 0x2A19) for connected LE devices that
///    did not get the property.
/// </summary>
public sealed partial class BluetoothProvider : IBatteryProvider
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string BluetoothClassAqs = "System.Devices.ClassGuid:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    const string AddressKey = "System.Devices.Aep.DeviceAddress";
    const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    const string AppearanceKey = "System.Devices.Aep.Bluetooth.Le.Appearance";

    public string Name => "Bluetooth";

    sealed record Endpoint(string Mac, string Name, bool IsLe, string AepId, DeviceKind Kind);

    public async Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var connected = await GetConnectedEndpointsAsync();
        if (connected.Count == 0) return [];

        var devnodeLevels = await GetDevnodeBatteryAsync();
        var results = new Dictionary<string, DeviceBattery>();

        foreach (var ep in connected)
        {
            ct.ThrowIfCancellationRequested();
            var id = "bt:" + ep.Mac;
            if (results.ContainsKey(id)) continue;

            var kind = ep.Kind != DeviceKind.Unknown ? ep.Kind : DeviceKindGuess.FromName(ep.Name);
            if (devnodeLevels.TryGetValue(ep.Mac, out var dn))
            {
                results[id] = new DeviceBattery
                {
                    Id = id, Name = ep.Name, Kind = kind,
                    Source = dn.IsLe ? BatterySource.BluetoothLE : BatterySource.Bluetooth,
                    Level = dn.Level,
                    Detail = dn.IsLe ? "BLE HID 电量属性" : "经典蓝牙 HFP 电量",
                };
                continue;
            }

            if (ep.IsLe && await ReadGattBatteryAsync(ep.AepId) is int gatt)
            {
                results[id] = new DeviceBattery
                {
                    Id = id, Name = ep.Name, Kind = kind, Source = BatterySource.BluetoothLE,
                    Level = gatt, Detail = "GATT 电池服务 0x2A19",
                };
            }
        }
        return results.Values.ToList();
    }

    static async Task<List<Endpoint>> GetConnectedEndpointsAsync()
    {
        string[] props = [IsConnectedKey, AddressKey, CodMajorKey, AppearanceKey];
        var list = new List<Endpoint>();
        foreach (var (aqs, isLe) in new[]
        {
            (BluetoothDevice.GetDeviceSelectorFromPairingState(true), false),
            (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true),
        })
        {
            var infos = await FindAllAsync(aqs, props, DeviceInformationKind.AssociationEndpoint);
            foreach (var info in infos)
            {
                if (Verbose)
                    Core.Log.Write($"  aep le={isLe} \"{info.Name}\" connected={Prop(info, IsConnectedKey)} " +
                                   $"major={Prop(info, CodMajorKey)} appearance={Prop(info, AppearanceKey)}");
                if (info.Properties.TryGetValue(IsConnectedKey, out var c) && c is true
                    && info.Properties.TryGetValue(AddressKey, out var a) && a is string addr)
                {
                    var kind = DeviceKind.Unknown;
                    if (info.Properties.TryGetValue(CodMajorKey, out var major) && major is ushort mj && mj != 0)
                        kind = DeviceKindGuess.FromClassOfDevice((uint)mj << 8);
                    else if (info.Properties.TryGetValue(AppearanceKey, out var ap) && ap is ushort apv)
                        kind = DeviceKindGuess.FromAppearance(apv);
                    list.Add(new Endpoint(NormalizeMac(addr), info.Name, isLe, info.Id, kind));
                }
            }
        }
        return list;
    }

    /// <summary>MAC → battery level read from BTHENUM\DEV_* / BTHLE\DEV_* devnodes.</summary>
    static async Task<Dictionary<string, (int Level, bool IsLe)>> GetDevnodeBatteryAsync()
    {
        var map = new Dictionary<string, (int, bool)>();
        var infos = await FindAllAsync(BluetoothClassAqs, [BatteryKey], DeviceInformationKind.Device);
        foreach (var info in infos)
        {
            if (!info.Properties.TryGetValue(BatteryKey, out var v) || v is not byte level) continue;
            var m = DevnodeMac().Match(info.Id);
            if (!m.Success) continue;
            bool isLe = info.Id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase);
            map[m.Groups[1].Value.ToUpperInvariant()] = (Math.Clamp((int)level, 0, 100), isLe);
        }
        return map;
    }

    static async Task<int?> ReadGattBatteryAsync(string aepId)
    {
        try
        {
            using var dev = await BluetoothLEDevice.FromIdAsync(aepId);
            if (dev is null || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
            var svcs = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) return null;
            try
            {
                var chars = await svcs.Services[0].GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) return null;
                var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success || read.Value.Length < 1) return null;
                return Math.Clamp((int)DataReader.FromBuffer(read.Value).ReadByte(), 0, 100);
            }
            finally
            {
                foreach (var s in svcs.Services) s.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Log every endpoint seen (set by --dump).</summary>
    public static bool Verbose { get; set; }

    static string Prop(DeviceInformation i, string key) =>
        i.Properties.TryGetValue(key, out var v) ? $"{v} ({v?.GetType().Name})" : "-";

    static readonly HashSet<string> RejectedKeys = new();

    /// <summary>
    /// FindAllAsync fails as a whole if any requested property key is unknown on this Windows
    /// build; drop rejected keys one at a time (remembering them) instead of losing everything.
    /// </summary>
    static async Task<DeviceInformationCollection> FindAllAsync(string aqs, string[] keys, DeviceInformationKind kind)
    {
        var wanted = keys.Where(k => !RejectedKeys.Contains(k)).ToList();
        try
        {
            return await DeviceInformation.FindAllAsync(aqs, wanted, kind);
        }
        catch (Exception ex)
        {
            Core.Log.Write($"Bluetooth: FindAll 0x{ex.HResult:X8} kind={kind} aqs={aqs}");
            foreach (var k in wanted)
            {
                try
                {
                    await DeviceInformation.FindAllAsync(aqs, [k], kind);
                }
                catch
                {
                    RejectedKeys.Add(k);
                    Core.Log.Write($"Bluetooth: property key rejected: {k}");
                }
            }
            return await DeviceInformation.FindAllAsync(aqs, keys.Where(k => !RejectedKeys.Contains(k)).ToList(), kind);
        }
    }

    static string NormalizeMac(string addr) => addr.Replace(":", "").Replace("-", "").ToUpperInvariant();

    [GeneratedRegex(@"DEV_([0-9A-Fa-f]{12})")]
    private static partial Regex DevnodeMac();
}
