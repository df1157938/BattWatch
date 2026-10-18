using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BattWatch.Core;

namespace BattWatch.UI;

public partial class MainWindow : Window
{
    readonly App _app = App.Current;
    bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Refresh();
        _loading = false;
    }

    // ---------- navigation ----------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDevices is null) return; // fires during InitializeComponent
        PageDevices.Visibility = NavDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavHistory.IsChecked == true) LoadHistoryDevices();
        if (NavSettings.IsChecked == true) LoadSettings();
    }

    // ---------- devices ----------

    public void Refresh()
    {
        var m = _app.Monitor;
        var list = m.Visible.Select(d => new DeviceView(d, _app.Settings.LowThreshold)).ToList();
        DeviceList.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 && m.LastRefresh != default ? Visibility.Visible : Visibility.Collapsed;
        DevicesSummary.Text = m.LastRefresh == default
            ? "正在读取…"
            : $"共 {list.Count} 台 · {m.LastRefresh:HH:mm:ss} 更新";
        if (PageHistory.Visibility == Visibility.Visible) DrawHistory();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        b.IsEnabled = false;
        try { await _app.Monitor.RefreshAsync(); }
        finally { b.IsEnabled = true; }
    }

    void HideDevice_Click(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Tag is not string id) return;
        if (!_app.Settings.HiddenIds.Contains(id)) _app.Settings.HiddenIds.Add(id);
        _app.SettingsChanged();
    }

    // ---------- history ----------

    void LoadHistoryDevices()
    {
        var selected = HistoryDevice.SelectedValue as string;
        var points = _app.History.Load(DateTime.Now.AddDays(-30));
        var devices = points.GroupBy(p => p.Id)
            .Select(g => new KeyValuePair<string, string>(g.Key, g.Last().Name))
            .OrderBy(kv => kv.Value).ToList();
        HistoryDevice.ItemsSource = devices;
        if (devices.Count == 0) { DrawHistory(); return; }
        HistoryDevice.SelectedValue = devices.Any(d => d.Key == selected) ? selected : devices[0].Key;
    }

    void HistoryDevice_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawHistory();
    void Range_Checked(object sender, RoutedEventArgs e) { if (Chart is not null) DrawHistory(); }

    void DrawHistory()
    {
        var span = Range7d.IsChecked == true ? TimeSpan.FromDays(7)
            : Range30d.IsChecked == true ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24);
        var from = DateTime.Now - span;
        var id = HistoryDevice.SelectedValue as string;
        var pts = id is null ? [] : _app.History.Load(from).Where(p => p.Id == id).OrderBy(p => p.Time).ToList();
        Chart.SetData(pts, from, DateTime.Now, _app.Settings.LowThreshold);

        if (pts.Count == 0)
        {
            StatNow.Text = StatMin.Text = StatRate.Text = StatEta.Text = "—";
            return;
        }
        StatNow.Text = $"{pts[^1].Level}%";
        StatMin.Text = $"{pts.Min(p => p.Level)}%";
        var rate = DrainPerHour(pts);
        StatRate.Text = rate is double r ? $"{r:0.0}%/时" : "—";
        StatEta.Text = rate is double r2 && r2 > 0.05 && !pts[^1].Charging ? FormatHours(pts[^1].Level / r2) : "—";
    }

    /// <summary>Average discharge rate over non-charging stretches (ignores charging segments).</summary>
    static double? DrainPerHour(List<HistoryPoint> pts)
    {
        double drop = 0, hours = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            if (a.Charging || b.Charging || b.Level > a.Level) continue;
            var h = (b.Time - a.Time).TotalHours;
            if (h <= 0 || h > 12) continue; // gaps = device off / PC asleep
            drop += a.Level - b.Level;
            hours += h;
        }
        return hours >= 0.25 ? drop / hours : null;
    }

    static string FormatHours(double h) =>
        h >= 48 ? $"{h / 24:0.#} 天" : h >= 1 ? $"{h:0.#} 小时" : $"{h * 60:0} 分钟";

    // ---------- settings ----------

    void LoadSettings()
    {
        bool wasLoading = _loading;
        _loading = true;
        var s = _app.Settings;
        Threshold.Value = s.LowThreshold;
        ThresholdText.Text = $"{s.LowThreshold}%";
        PollInterval.SelectedItem = PollInterval.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.PollSeconds.ToString()) ?? PollInterval.Items[1];
        AutoStart.IsChecked = Settings.AutoStart;
        ShowLaptop.IsChecked = s.ShowLaptopBattery;
        ShowWidget.IsChecked = _app.WidgetVisible;

        var names = _app.Monitor.Devices.ToDictionary(d => d.Id, d => d.Name);
        HiddenList.ItemsSource = s.HiddenIds
            .Select(id => new KeyValuePair<string, string>(id, names.TryGetValue(id, out var n) ? $"{n}  ({id})" : id))
            .ToList();
        HiddenEmpty.Visibility = s.HiddenIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = wasLoading;
    }

    void Threshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText is null) return;
        ThresholdText.Text = $"{(int)e.NewValue}%";
        if (_loading) return;
        _app.Settings.LowThreshold = (int)e.NewValue;
        _app.SettingsChanged();
    }

    void PollInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PollInterval.SelectedItem is not ComboBoxItem { Tag: string t }) return;
        _app.Settings.PollSeconds = int.Parse(t);
        _app.SettingsChanged();
    }

    void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try { Settings.AutoStart = AutoStart.IsChecked == true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "设置开机启动失败"); }
        AutoStart.IsChecked = Settings.AutoStart;
    }

    void ShowLaptop_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.ShowLaptopBattery = ShowLaptop.IsChecked == true;
        _app.SettingsChanged();
    }

    void ShowWidget_Click(object sender, RoutedEventArgs e) => _app.SetWidgetVisible(ShowWidget.IsChecked == true);

    void Unhide_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string id) return;
        _app.Settings.HiddenIds.Remove(id);
        _app.SettingsChanged();
        LoadSettings();
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", Settings.DataDir) { UseShellExecute = true });
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定删除全部电量历史记录？此操作不可恢复。", "清空历史",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var n = _app.History.Clear();
        MessageBox.Show(this, $"已删除 {n} 个历史文件。", "清空历史");
    }
}
