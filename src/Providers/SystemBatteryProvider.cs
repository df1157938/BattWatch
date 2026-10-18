using BattWatch.Core;
using Windows.Devices.Power;
using Windows.System.Power;

namespace BattWatch.Providers;

/// <summary>The laptop's own battery (aggregate of all internal packs).</summary>
public sealed class SystemBatteryProvider : IBatteryProvider
{
    public string Name => "本机电池";

    public Task<IReadOnlyList<DeviceBattery>> ReadAsync(CancellationToken ct)
    {
        var report = Battery.AggregateBattery.GetReport();
        if (report.Status == BatteryStatus.NotPresent
            || report.FullChargeCapacityInMilliwattHours is not int full || full <= 0
            || report.RemainingCapacityInMilliwattHours is not int remaining)
            return Task.FromResult<IReadOnlyList<DeviceBattery>>([]);

        var level = (int)Math.Round(remaining * 100.0 / full);
        var rate = report.ChargeRateInMilliwatts;
        return Task.FromResult<IReadOnlyList<DeviceBattery>>(
        [
            new DeviceBattery
            {
                Id = "system:battery",
                Name = "笔记本电池",
                Kind = DeviceKind.Laptop,
                Source = BatterySource.System,
                Level = Math.Clamp(level, 0, 100),
                IsCharging = report.Status == BatteryStatus.Charging,
                Detail = rate is int r && r != 0 ? $"{(r > 0 ? "充电" : "放电")} {Math.Abs(r) / 1000.0:0.0} W" : "Windows 电池",
            },
        ]);
    }
}
