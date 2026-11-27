using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MapEditor;

/// "Please wait" bar (the user's request 2026-11-14): while something runs for longer, a card with a percentage bar appears over the
/// whole window and nothing else can be clicked or typed. Short work (brush strokes …) never shows the card, only the blocking.
/// Work that reports real fractions via IWorkProgress (BusyProgress) drives the bar directly (calibrated per phase, never jumping
/// backwards and never showing 100 % before completion); work without fractions falls back to the remembered duration of the same
/// work (busy_times.json next to the settings; a first run assumes 10 s).
public partial class MainWindow
{
    const double BusyDelay = 0.4;          // seconds until the card appears
    const double BusyDefaultSeconds = 10;  // expected duration of work seen for the first time

    readonly DispatcherTimer busyTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Stopwatch busyWatch = new();
    bool busyOn;          // work is running, window blocked
    bool overlayMessage;  // the old overlay shows a message (no world / error), not work
    string busyKey;       // name of the work for the remembered duration
    bool busyKeyFromText;
    string busyTitle;     // optional heading shown above the current sub-step on the card
    string busyStatus;    // live text directly for BusyDetail (works even without Overlay)
    double? busyFraction; // real reported progress (0..1), null = time-based fallback
    double busyShown;     // monotonic fraction currently shown (0..0.99)
    int busySession;      // increments whenever a new busy work ends/starts so late Dispatcher posts are ignored
    double busyExpected = BusyDefaultSeconds;
    Dictionary<string, double> busyHistory;

    void InitBusy()
    {
        Map.BusyChanged += UpdateBusy;
        Overlay.IsVisibleChanged += (_, _) => UpdateBusy();
        busyTimer.Tick += (_, _) => BusyTick();
        PreviewKeyDown += (_, e) =>   // no keys while working (also no shortcuts); only Esc = Cancel, if the work can be cancelled
        {
            if (!busyOn) return;
            if (e.Key == Key.Escape) RequestCancel();
            e.Handled = true;
        };
    }

    /// start a busy progress reporter that shows the live status text and real progress fraction directly on BusyCard
    /// (and optionally forwards the sub-step text to another window or status label via <paramref name="also"/>).
    /// Call right before setting Map.Busy = true or ShowWork().
    IWorkProgress BusyProgress(string title, bool cancelable = true, Action<string> also = null)
    {
        int session = ++busySession;
        busyTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        busyStatus = busyTitle;
        busyFraction = null;
        busyShown = 0;
        if (busyTitle != null)
        {
            busyKey = busyTitle;
            busyKeyFromText = true;
            busyExpected = History(busyKey) ?? BusyDefaultSeconds;
        }
        Action<string, double?> show = (text, frac) =>
        {
            if (session != busySession) return;
            string sub = (text ?? "").Trim();
            busyStatus = busyTitle == null ? sub : (sub.Length == 0 ? busyTitle : busyTitle + "\n" + sub);
            if (!busyKeyFromText && sub.Length > 0)
            {
                busyKeyFromText = true;
                busyKey = sub.Split('\n')[0];
                busyExpected = History(busyKey) ?? BusyDefaultSeconds;
            }
            if (frac is double f) busyFraction = Math.Clamp(f, 0, 1);
            also?.Invoke(text);
            if (BusyCard.Visibility == Visibility.Visible) RefreshBusyCard(busyWatch.Elapsed.TotalSeconds);
        };
        return cancelable ? WorkCancel.Progress(show) : WorkCancel.Observer(show);
    }

    /// set a descriptive status text directly on BusyCard for short/single-phase Map.Busy operations without IProgress
    void SetBusyTitle(string title)
    {
        busySession++;
        busyTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        busyStatus = busyTitle;
        busyFraction = null;
        busyShown = 0;
        if (busyTitle != null)
        {
            busyKey = busyTitle;
            busyKeyFromText = true;
            busyExpected = History(busyKey) ?? BusyDefaultSeconds;
        }
        if (BusyCard.Visibility == Visibility.Visible) RefreshBusyCard(busyWatch.Elapsed.TotalSeconds);
    }

    /// show the old overlay for work (with progress texts): the card with the bar replaces its text
    void ShowWork()
    {
        overlayMessage = false;
        LoadingText.Text = "";   // no stale text from the last work (it names the work for the remembered duration)
        Overlay.Visibility = Visibility.Visible;
        UpdateBusy();   // the overlay may already be visible with a message (project without a map): then no visibility event comes
    }

    /// show the old overlay with a message (no world, load error): no bar, no blocking of the ribbon
    void ShowOverlayMessage()
    {
        overlayMessage = true;
        Overlay.Visibility = Visibility.Visible;
        UpdateBusy();
    }

    void UpdateBusy()
    {
        if (!IsInitialized) return;
        bool overlayWork = Overlay.IsVisible && !overlayMessage;
        LoadingText.Visibility = overlayWork ? Visibility.Collapsed : Visibility.Visible;
        bool work = Map.Busy || overlayWork;
        if (work == busyOn) return;
        busyOn = work;
        BlockOtherWindows(work);
        if (work)
        {
            busyWatch.Restart();
            if (busyTitle == null && busyStatus == null)
            {
                busyKey = "tool:" + (ActiveTool() ?? "");
                busyKeyFromText = false;
                busyExpected = History(busyKey) ?? BusyDefaultSeconds;
                busyFraction = null;
                busyShown = 0;
            }
            BusyLayer.Background = Brushes.Transparent;   // blocks input at once, dims the window only when the card appears
            BusyCard.Visibility = Visibility.Collapsed;
            BusyLayer.Visibility = Visibility.Visible;
            Keyboard.Focus(BusyLayer);
            BusyCancelButton.IsEnabled = true;   // (WorkCancel is reset when the work is over; work may have marked itself as cancelable before the card started)
            BusyCancelButton.Content = Loc.T("Cancel");
            BusyCancelButton.Visibility = Visibility.Collapsed;
            busyTimer.Start();
        }
        else
        {
            WorkCancel.Reset();
            busyTimer.Stop();
            double seconds = busyWatch.Elapsed.TotalSeconds;
            if (seconds >= BusyDelay) RememberDuration(busyKey, seconds);
            busySession++;
            busyTitle = null;
            busyStatus = null;
            busyFraction = null;
            busyShown = 0;
            BusyLayer.Visibility = Visibility.Collapsed;
            BusyCard.Visibility = Visibility.Collapsed;
            if (IsActive) Map.Focus();
        }
    }

    void BusyTick()
    {
        double t = busyWatch.Elapsed.TotalSeconds;
        string status = CurrentBusyStatus();
        if (!busyKeyFromText && status.Length > 0)
        {
            // the first text names the work (stable between runs, e.g. "Applying road …"); with a world size inside for loading
            busyKeyFromText = true;
            busyKey = status.Split('\n')[0];
            busyExpected = History(busyKey) ?? BusyDefaultSeconds;
        }
        if (t < BusyDelay) return;
        if (BusyCard.Visibility != Visibility.Visible)
        {
            BusyLayer.SetResourceReference(Border.BackgroundProperty, "OverlayBrush");
            BusyCard.Visibility = Visibility.Visible;
        }
        RefreshBusyCard(t);
    }

    string CurrentBusyStatus()
    {
        if (!string.IsNullOrEmpty(busyStatus)) return busyStatus;
        return Overlay.IsVisible && !overlayMessage ? (LoadingText.Text ?? "").Trim() : "";
    }

    void RefreshBusyCard(double t)
    {
        double p;
        if (busyFraction is double f)
        {
            busyShown = Math.Max(busyShown, Math.Clamp(f, 0, 0.99));
            p = busyShown;
        }
        else
        {
            // 0–90 % linear over the expected time, then slowly towards (never reaching) 99 %
            double e = Math.Max(busyExpected, 1);
            double est = t < e ? 0.9 * t / e : 0.9 + 0.09 * (1 - Math.Exp(-(t - e) / e));
            busyShown = Math.Max(busyShown, est);
            p = busyShown;
        }
        string status = CurrentBusyStatus();
        BusyBar.Value = p * 100;
        BusyPercent.Text = Math.Clamp((int)Math.Round(p * 100), 0, 99) + " %";
        BusyDetail.Text = status;
        BusyDetail.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyCancelButton.Visibility = WorkCancel.Cancelable ? Visibility.Visible : Visibility.Collapsed;   // only work that can be cancelled safely
    }

    // ---- remembered durations ----

    static string BusyFile
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("MAPEDITOR_SETTINGS");
            string dir = env != null ? Path.GetDirectoryName(Path.GetFullPath(env)) : AppSettings.DataFolder;
            return Path.Combine(dir, "busy_times.json");
        }
    }

    double? History(string key)
    {
        if (busyHistory == null)
        {
            try { busyHistory = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(BusyFile)) ?? new(); }
            catch { busyHistory = new(); }
        }
        return key != null && busyHistory.TryGetValue(key, out var s) && s > 0 ? s : null;
    }

    void RememberDuration(string key, double seconds)
    {
        if (key == null) return;
        History(key);   // load
        busyHistory[key] = Math.Round(seconds, 1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BusyFile));
            File.WriteAllText(BusyFile, JsonSerializer.Serialize(busyHistory));
        }
        catch { /* only a convenience */ }
    }
}

public partial class MainWindow
{
    void BusyCancel_Click(object sender, RoutedEventArgs e) => RequestCancel();

    /// "Cancel" on the card or Esc: only a flag; the work stops at its next check and undoes what it has done so far (see WorkCancel)
    void RequestCancel()
    {
        if (!busyOn || !WorkCancel.Cancelable || WorkCancel.Requested) return;
        WorkCancel.Request();
        BusyCancelButton.IsEnabled = false;
        BusyCancelButton.Content = Loc.T("Cancelling …");
    }
}

public partial class MainWindow
{
    readonly List<Window> blockedWindows = new();

    /// while the progress card is shown, nothing may be done anywhere (the user's request 2026-11-20): the layer only covers this window,
    /// so the other windows of the program (road network, wilderness, steps, spawn points, buildings …) are switched off meanwhile
    void BlockOtherWindows(bool block)
    {
        if (block)
        {
            foreach (var w in Application.Current.Windows.OfType<Window>())
                if (w != this && w.IsVisible && w.IsEnabled) { w.IsEnabled = false; blockedWindows.Add(w); }
        }
        else
        {
            foreach (var w in blockedWindows) w.IsEnabled = true;
            blockedWindows.Clear();
        }
    }
}
