using System.Windows.Threading;

namespace CodexBar.App.Hud;

/// <summary>
/// Follows the built-in screen's brightness through WMI (<c>WmiMonitorBrightnessEvent</c>) — the
/// brightness keys, Quick Settings, power plans. Desktops with an external monitor have no such
/// event, and the watcher then simply stays quiet. Changes arrive on the UI thread as 0–100.
/// </summary>
internal sealed class BrightnessWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private volatile bool _stopped;
    private Thread? _thread;

    public BrightnessWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action<int>? Changed;

    public void Start()
    {
        _thread = new Thread(Listen) { IsBackground = true, Name = "Brightness watcher" };
        _thread.SetApartmentState(ApartmentState.STA);   // the WMI scripting objects are apartment-threaded
        _thread.Start();
    }

    private void Listen()
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                return;
            }

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic service = locator.ConnectServer(".", @"root\wmi");
            dynamic events = service.ExecNotificationQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            while (!_stopped)
            {
                dynamic? next = null;
                try
                {
                    next = events.NextEvent(1000);
                }
                catch (System.Runtime.InteropServices.COMException exception) when ((uint)exception.HResult == 0x80043001)
                {
                    continue;   // wbemErrTimedout: nothing happened this second
                }

                if (next is not null)
                {
                    int level = Convert.ToInt32(next.Brightness);
                    _dispatcher.BeginInvoke(() => Changed?.Invoke(level));
                }
            }
        }
        catch (Exception exception)
        {
            // No built-in panel (or WMI unavailable): no brightness HUD, nothing else affected.
            CodexBar.Core.DiagnosticsLog.Error("Brightness", exception.Message);
        }
    }

    public void Dispose() => _stopped = true;
}
