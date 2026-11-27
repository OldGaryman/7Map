using System.Windows.Threading;

namespace MapEditor;

/// Progress reporter that can carry both a status text and an optional known fraction (0..1) of the whole work.
/// Extends IProgress&lt;string&gt; so all existing callers, tests and helpers stay source-compatible.
public interface IWorkProgress : IProgress<string>
{
    void Report(string text, double fraction);
}

public static class ProgressExtensions
{
    /// report status text together with a real progress fraction (0..1); falls back to text-only on plain IProgress&lt;string&gt;
    public static void Report(this IProgress<string> progress, string text, double fraction)
    {
        if (progress is IWorkProgress wp) wp.Report(text, fraction);
        else progress?.Report(text);
    }

    /// map an inner [0..1] progress range onto [from..to] of the outer progress
    public static IProgress<string> Slice(this IProgress<string> progress, double from, double to) =>
        progress == null ? null : new SlicedProgress(progress, from, to);

    sealed class SlicedProgress : IWorkProgress
    {
        readonly IProgress<string> outer;
        readonly double from, span;

        public SlicedProgress(IProgress<string> outer, double from, double to)
        {
            this.outer = outer;
            this.from = Math.Clamp(from, 0, 1);
            span = Math.Max(0, Math.Clamp(to, 0, 1) - this.from);
        }

        public void Report(string value) => outer.Report(value);

        public void Report(string text, double fraction)
        {
            double mapped = from + span * Math.Clamp(fraction, 0, 1);
            if (outer is IWorkProgress wp) wp.Report(text, mapped);
            else outer.Report(text);
        }
    }
}

/// "Cancel" for the progress bar (the user's request 2026-11-20). How it works:
///  - Work that can be cancelled safely marks itself (Cancelable): the edit steps (WorldEdits.Run), the planning of towns, roads and
///    wilderness, creating a map and loading a map. Only then the card shows the "Cancel" button. Saving and copying into the game
///    are never cancelable (half-written files).
///  - Cancel only sets a flag. The work notices it at its next change of the world (edit steps) or at its next progress text and
///    throws OperationCanceledException. An edit step is undone completely by the exception (WorldEdits.Run), planning has not
///    changed anything yet, a half-created map folder is deleted.
///  - The exception is not an error: the windows do not show a message for it (IsCancel) and the application only shows "Cancelled".
public static class WorkCancel
{
    static volatile bool requested, cancelable;

    public static bool Requested => requested;
    public static bool Cancelable => cancelable;

    /// the running work can be cancelled from now on (the card shows the button)
    public static void MarkCancelable() => cancelable = true;

    public static void Request() { if (cancelable) requested = true; }

    /// new work starts or the work is over
    public static void Reset() { requested = false; cancelable = false; }

    public static void ThrowIfRequested()
    {
        if (requested) throw new OperationCanceledException();
    }

    public static bool IsCancel(Exception ex) => ex is OperationCanceledException || (ex is AggregateException a && a.InnerExceptions.All(IsCancel));

    /// progress texts that also check for the cancel request: every text the work reports is a place where it can stop
    public static IWorkProgress Progress(Action<string> show) => Progress((t, _) => show(t));

    /// cancelable progress with optional real fraction (0..1)
    public static IWorkProgress Progress(Action<string, double?> show)
    {
        MarkCancelable();
        return new WorkProgress(show, checkCancel: true);
    }

    /// non-cancelable progress with optional real fraction (for Save, Copy, etc.)
    public static IWorkProgress Observer(Action<string, double?> show) => new WorkProgress(show, checkCancel: false);

    sealed class WorkProgress : IWorkProgress
    {
        readonly Action<string, double?> show;
        readonly bool checkCancel;
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

        public WorkProgress(Action<string, double?> show, bool checkCancel)
        {
            this.show = show;
            this.checkCancel = checkCancel;
        }

        public void Report(string text) => Deliver(text, null);

        public void Report(string text, double fraction) => Deliver(text, Math.Clamp(fraction, 0, 1));

        void Deliver(string text, double? fraction)
        {
            if (checkCancel) ThrowIfRequested();
            if (dispatcher.CheckAccess()) show(text, fraction);
            else dispatcher.BeginInvoke(() => show(text, fraction));
        }
    }
}
