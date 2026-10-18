using System.Windows;
using BattWatch.Core;
using BattWatch.UI;

namespace BattWatch;

public partial class App : Application
{
    Mutex? _single;
    public Settings Settings { get; private set; } = null!;
    public HistoryStore History { get; } = new();
    public BatteryMonitor Monitor { get; private set; } = null!;
    TrayIcon? _tray;
    MainWindow? _main;
    WidgetWindow? _widget;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Write("UI: " + ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Task: " + ex.Exception); ex.SetObserved(); };
        Log.Write($"start {string.Join(' ', e.Args)}");

        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            SnapshotAsync(e.Args[1]).ContinueWith(t =>
            {
                if (t.Exception is not null) Log.Write("Snapshot: " + t.Exception);
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        if (e.Args.Contains("--dump"))
        {
            DumpAsync().ContinueWith(t =>
            {
                if (t.Exception is not null) Log.Write("Dump: " + t.Exception);
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }
        _single = new Mutex(true, @"Local\BattWatch.SingleInstance", out bool first);
        if (!first)
        {
            // Ask the running instance to show itself, then quit.
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }
        SingleInstance.Listen(() => Dispatcher.Invoke(ShowMain));

        Settings = Settings.Load();
        Monitor = new BatteryMonitor(Settings, History);
        _tray = new TrayIcon(this);
        Monitor.Updated += () => Dispatcher.BeginInvoke(OnUpdated);
        Monitor.LowBattery += d => Dispatcher.BeginInvoke(() => _tray?.NotifyLow(d, Settings.LowThreshold));
        Monitor.Start();

        if (Settings.ShowWidget) SetWidgetVisible(true);
        if (!e.Args.Contains("--tray")) ShowMain();
    }

    /// <summary>Diagnostics: read every provider, write the result to dump.txt in the data folder, exit.</summary>
    async Task DumpAsync()
    {
        Settings = Settings.Load();
        Providers.BluetoothProvider.Verbose = true;
        Providers.MchoseHidProvider.Verbose = true;
        Monitor = new BatteryMonitor(Settings, new HistoryStore());
        await Monitor.RefreshAsync();
        var lines = Monitor.Devices.Select(d =>
            $"{d.Id}\t{d.Name}\t{d.Kind}\t{d.SourceLabel}\t{d.Level}%\tcharging={d.IsCharging}\t{d.Detail}");
        System.IO.Directory.CreateDirectory(Settings.DataDir);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(Settings.DataDir, "dump.txt"),
            lines.DefaultIfEmpty("(no devices)"));
        Monitor.Dispose();
    }

    /// <summary>Diagnostics: render each main-window page and the widget to PNGs in <paramref name="dir"/>.</summary>
    async Task SnapshotAsync(string dir)
    {
        await Dispatcher.InvokeAsync(() => { Settings = Settings.Load(); Monitor = new BatteryMonitor(Settings, History); });
        await Monitor.RefreshAsync();
        await Dispatcher.InvokeAsync(async () =>
        {
            System.IO.Directory.CreateDirectory(dir);
            var main = new MainWindow { Left = -10000, Top = -10000, ShowActivated = false };
            main.Show();
            foreach (var (nav, name) in new[] { (main.NavDevices, "devices"), (main.NavHistory, "history"), (main.NavSettings, "settings") })
            {
                nav.IsChecked = true;
                await Task.Delay(300);
                Snapshot.Save(main, System.IO.Path.Combine(dir, name + ".png"));
            }
            main.Close();
            var widget = new WidgetWindow { Left = -10000, Top = -10000, ShowActivated = false };
            widget.Show();
            await Task.Delay(300);
            Snapshot.Save(widget, System.IO.Path.Combine(dir, "widget.png"));
            widget.Close();
        }).Task.Unwrap();
        Monitor.Dispose();
    }

    void OnUpdated()
    {
        _tray?.Update(Monitor.Visible.ToList());
        _main?.Refresh();
        _widget?.Refresh();
    }

    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
            _main.Show();
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public bool WidgetVisible => _widget is not null;

    public void SetWidgetVisible(bool visible)
    {
        if (visible && _widget is null)
        {
            _widget = new WidgetWindow();
            _widget.Closed += (_, _) => _widget = null;
            _widget.Show();
        }
        else if (!visible && _widget is not null)
        {
            _widget.Close();
        }
        if (Settings.ShowWidget != visible)
        {
            Settings.ShowWidget = visible;
            Settings.Save();
        }
    }

    public void SettingsChanged()
    {
        Settings.Save();
        OnUpdated();
        _ = Monitor.RefreshAsync();
    }

    public void Quit()
    {
        Monitor.Dispose();
        _tray?.Dispose();
        Shutdown();
    }
}
