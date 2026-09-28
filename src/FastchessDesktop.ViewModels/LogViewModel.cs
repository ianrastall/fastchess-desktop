using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastchessDesktop.ViewModels.Services;

namespace FastchessDesktop.ViewModels;

public enum LogKind
{
    Info,
    Command,
    Output,
    Error,
    Success,
}

public sealed record LogEntry(DateTime Time, LogKind Kind, string Text)
{
    public string Display => $"{Time:HH:mm:ss}  {Text}";
}

/// <summary>
/// Thread-safe log. Lines may be added from any thread; they are appended to <see cref="Lines"/>
/// on the UI thread in batches, and the oldest lines are dropped beyond <see cref="MaxLines"/>.
/// </summary>
public sealed partial class LogViewModel(IUiDispatcher dispatcher) : ObservableObject
{
    public const int MaxLines = 5000;

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private int _flushScheduled;

    public ObservableCollection<LogEntry> Lines { get; } = [];

    /// <summary>Raised on the UI thread after new lines were appended (for auto-scroll).</summary>
    public event EventHandler? LinesAppended;

    public void Add(string text, LogKind kind = LogKind.Info)
    {
        _pending.Enqueue(new LogEntry(DateTime.Now, kind, text));
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0) dispatcher.Post(Flush);
    }

    public void Error(string text) => Add(text, LogKind.Error);

    public string AllText => string.Join(Environment.NewLine, Lines.Select(l => l.Display));

    [RelayCommand]
    private void Clear() => Lines.Clear();

    private void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        var added = false;
        while (_pending.TryDequeue(out var entry))
        {
            Lines.Add(entry);
            added = true;
        }
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        if (added) LinesAppended?.Invoke(this, EventArgs.Empty);
    }
}
