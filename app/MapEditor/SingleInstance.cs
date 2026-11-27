using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MapEditor;

/// Ensures that YAMEE runs only once per Windows session.
/// If a second instance starts, it silently asks the running instance to show its window and exits without any message.
internal sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "Local\\YAMEE.SingleInstance";

    const uint ASFW_ANY = unchecked((uint)-1);
    const int SW_RESTORE = 9;
    const int SW_SHOW = 5;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint dwProcessId);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    readonly Mutex mutex;
    readonly EventWaitHandle activateEvent;
    readonly EventWaitHandle ackEvent;
    readonly ManualResetEventSlim stopEvent;
    readonly Thread listenerThread;
    bool disposed;

    SingleInstance(Mutex mutex, EventWaitHandle activateEvent, EventWaitHandle ackEvent, Action onActivate)
    {
        this.mutex = mutex;
        this.activateEvent = activateEvent;
        this.ackEvent = ackEvent;
        if (activateEvent == null) return;

        stopEvent = new ManualResetEventSlim(false);
        listenerThread = new Thread(() => Listen(onActivate))
        {
            Name = "YAMEE.SingleInstance",
            IsBackground = true
        };
        listenerThread.Start();
    }

    /// Tries to claim the single-instance lock.
    /// Returns an instance (keep alive until exit) if this is the first running copy;
    /// otherwise signals the running copy to bring its window to the front and returns null.
    public static SingleInstance TryStart(Action onActivate, string name = DefaultName)
    {
        Mutex mutex = null;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name, out createdNew);
        }
        catch (Exception)
        {
            mutex?.Dispose();
            return new SingleInstance(null, null, null, null);
        }

        if (!createdNew)
        {
            mutex.Dispose();
            SignalExisting(name);
            return null;
        }

        EventWaitHandle activate = null;
        EventWaitHandle ack = null;
        try
        {
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName(name));
            ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckEventName(name));
        }
        catch (Exception)
        {
            activate?.Dispose();
            ack?.Dispose();
            activate = null;
            ack = null;
        }

        return new SingleInstance(mutex, activate, ack, onActivate);
    }

    static string ActivateEventName(string name) => name + ".Activate";
    static string AckEventName(string name) => name + ".Ack";

    static void SignalExisting(string name)
    {
        try
        {
            // Hand over this new process's foreground permission so the running window can reliably surface.
            AllowSetForegroundWindow(ASFW_ANY);

            EventWaitHandle activate = null;
            for (int i = 0; i < 20; i++)
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName(name), out activate)) break;
                Thread.Sleep(25);
            }
            if (activate == null) return;

            using (activate)
            {
                EventWaitHandle ack = null;
                EventWaitHandle.TryOpenExisting(AckEventName(name), out ack);
                using (ack)
                {
                    ack?.Reset();
                    activate.Set();
                    // Stay alive briefly while the running instance calls SetForegroundWindow.
                    ack?.WaitOne(300);
                }
            }
        }
        catch (Exception)
        {
            // Never show an error when handing focus to an already-running instance.
        }
    }

    void Listen(Action onActivate)
    {
        var handles = new WaitHandle[] { stopEvent.WaitHandle, activateEvent };
        while (true)
        {
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (signaled == 0) return;

            try { onActivate?.Invoke(); }
            catch (Exception) { }
            try { ackEvent?.Set(); }
            catch (Exception) { }
        }
    }

    /// Brings the running app's main window (or its active modal dialog) to the foreground without un-maximizing it.
    public static void BringToFront(Application app, Func<IntPtr> getKnownHandle)
    {
        if (app == null) return;

        // First trigger via Win32 directly so it also works while a native modal dialog (e.g. MessageBox) blocks the Dispatcher.
        IntPtr known = getKnownHandle?.Invoke() ?? IntPtr.Zero;
        if (known != IntPtr.Zero) ActivateHandle(known, asyncShow: true);

        try
        {
            var op = app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                var main = app.MainWindow;
                if (main == null) return;

                IntPtr hwnd = new WindowInteropHelper(main).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    if (!main.IsVisible) main.Show();
                    main.Activate();
                    return;
                }

                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                else if (!main.IsVisible) main.Show();

                IntPtr target = GetLastActivePopup(hwnd);
                if (target == IntPtr.Zero) target = hwnd;
                SetForegroundWindow(target);

                if (target == hwnd)
                {
                    main.Activate();
                    if (GetForegroundWindow() != hwnd)
                    {
                        bool wasTopmost = main.Topmost;
                        main.Topmost = true;
                        main.Topmost = wasTopmost;
                        main.Activate();
                        SetForegroundWindow(hwnd);
                    }
                }
            }));
            op.Wait(TimeSpan.FromMilliseconds(150));
        }
        catch (Exception) { }
    }

    static void ActivateHandle(IntPtr hwnd, bool asyncShow)
    {
        if (IsIconic(hwnd))
        {
            if (asyncShow) ShowWindowAsync(hwnd, SW_RESTORE);
            else ShowWindow(hwnd, SW_RESTORE);
        }
        else if (!IsWindowVisible(hwnd))
        {
            if (asyncShow) ShowWindowAsync(hwnd, SW_SHOW);
            else ShowWindow(hwnd, SW_SHOW);
        }

        IntPtr target = GetLastActivePopup(hwnd);
        if (target == IntPtr.Zero) target = hwnd;
        SetForegroundWindow(target);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { stopEvent?.Set(); } catch (Exception) { }
        try { listenerThread?.Join(200); } catch (Exception) { }
        try { activateEvent?.Dispose(); } catch (Exception) { }
        try { ackEvent?.Dispose(); } catch (Exception) { }
        try { stopEvent?.Dispose(); } catch (Exception) { }
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
            try { mutex.Dispose(); } catch (Exception) { }
        }
    }
}
