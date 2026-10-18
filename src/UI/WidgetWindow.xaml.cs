using System.Windows;
using System.Windows.Input;

namespace BattWatch.UI;

public partial class WidgetWindow : Window
{
    readonly App _app = App.Current;

    public WidgetWindow()
    {
        InitializeComponent();
        var s = _app.Settings;
        if (!double.IsNaN(s.WidgetLeft) && !double.IsNaN(s.WidgetTop)
            && s.WidgetLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && s.WidgetTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20)
        {
            Left = s.WidgetLeft;
            Top = s.WidgetTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - 240;
            Top = SystemParameters.WorkArea.Top + 80;
        }
        Refresh();
    }

    public void Refresh()
    {
        var list = _app.Monitor.Visible.Select(d => new DeviceView(d, _app.Settings.LowThreshold)).ToList();
        Items.ItemsSource = list;
        Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { _app.ShowMain(); return; }
        DragMove();
        _app.Settings.WidgetLeft = Left;
        _app.Settings.WidgetTop = Top;
        _app.Settings.Save();
    }

    void Open_Click(object sender, RoutedEventArgs e) => _app.ShowMain();
    void Close_Click(object sender, RoutedEventArgs e) => _app.SetWidgetVisible(false);
}
