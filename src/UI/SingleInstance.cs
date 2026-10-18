namespace BattWatch.UI;

/// <summary>Second launch signals the first one to show its window.</summary>
static class SingleInstance
{
    const string EventName = @"Local\BattWatch.Show";

    public static void SignalExisting()
    {
        if (EventWaitHandle.TryOpenExisting(EventName, out var h))
            using (h) h.Set();
    }

    public static void Listen(Action onSignal)
    {
        var h = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        new Thread(() =>
        {
            while (h.WaitOne()) onSignal();
        }) { IsBackground = true, Name = "single-instance" }.Start();
    }
}
