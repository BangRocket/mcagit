namespace McaDiff.Output;

/// <summary>
/// Git-style progress on stderr: one line repainted in place with '\r', throttled to ~10 Hz, with a
/// trailing ", done." when a phase ends. Auto-disabled when stderr isn't an interactive terminal (pipes,
/// CI, machine-readable <c>--json</c> consumers stay clean) or when <c>NO_PROGRESS</c> is set — the same
/// spirit as <see cref="Ansi.ShouldColor"/>. Thread-safe: the parallel snapshot reports from many threads.
/// A disabled instance is a no-op on every call, so callers can wire it unconditionally.
/// </summary>
public sealed class Progress
{
    private readonly bool _on;
    private readonly object _gate = new();
    private string _label = "";
    private long _nextPaintTick;
    private int _painted; // chars on the current line, so a shorter next line can erase the tail

    public Progress(bool on) => _on = on;

    /// <summary>Show progress only when stderr is an interactive terminal and NO_PROGRESS is unset.</summary>
    public static bool ShouldShow() =>
        !Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_PROGRESS"));

    /// <summary>Begins a phase; subsequent <see cref="Update"/>/<see cref="Done"/> paint under <paramref name="label"/>.</summary>
    public void Begin(string label)
    {
        if (!_on) return;
        lock (_gate) { _label = label; _nextPaintTick = 0; _painted = 0; }
    }

    /// <summary>Throttled repaint — safe to call on every item from any thread.</summary>
    public void Update(long current, long total, string? extra = null)
    {
        if (!_on) return;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now < _nextPaintTick) return;
            _nextPaintTick = now + 100; // ~10 repaints/sec
            Paint(current, total, extra, done: false);
        }
    }

    /// <summary>Final repaint for the phase: appends ", done." and a newline.</summary>
    public void Done(long current, long total, string? extra = null)
    {
        if (!_on) return;
        lock (_gate) Paint(current, total, extra, done: true);
    }

    private void Paint(long current, long total, string? extra, bool done)
    {
        string body = total > 0
            ? $"{_label}: {100 * current / total,3}% ({current}/{total})"
            : $"{_label}: {current}";
        if (extra is not null) body += $", {extra}";
        if (done) body += ", done.";
        int pad = Math.Max(0, _painted - body.Length);
        Console.Error.Write('\r' + body + new string(' ', pad) + (done ? "\n" : ""));
        _painted = done ? 0 : body.Length;
    }
}
