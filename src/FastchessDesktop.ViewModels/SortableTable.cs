using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FastchessDesktop.ViewModels;

/// <summary>A sortable column: <paramref name="Compare"/> orders rows ascending.</summary>
public sealed record TableColumn<TRow>(string Key, Comparison<TRow> Compare, bool DescendingFirst = false);

/// <summary>
/// Rows kept in the order of the selected column. Rows are view models that update their own
/// cells; <see cref="Refresh"/> then moves only the rows whose position changed, so a live table
/// does not flicker the way clearing and refilling it would.
/// </summary>
public class SortableTable<TRow> : ObservableObject where TRow : class
{
    private readonly Dictionary<string, TableColumn<TRow>> _columns;
    private readonly Comparison<TRow> _tieBreak;
    private string _sortKey;
    private bool _sortDescending;

    /// <param name="columns">Sortable columns; the first is the initial sort.</param>
    /// <param name="tieBreak">Order for rows the selected column considers equal.</param>
    public SortableTable(IEnumerable<TableColumn<TRow>> columns, Comparison<TRow> tieBreak)
    {
        _columns = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _tieBreak = tieBreak;
        var first = _columns.Values.First();
        _sortKey = first.Key;
        _sortDescending = first.DescendingFirst;
        SortCommand = new RelayCommand<string>(Sort);
    }

    public ObservableCollection<TRow> Rows { get; } = [];

    public string SortKey
    {
        get => _sortKey;
        private set => SetProperty(ref _sortKey, value);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    /// <summary>Header click: sorts by the column, or reverses the order if it is already the sort column.</summary>
    public IRelayCommand<string> SortCommand { get; }

    public void Sort(string? key)
    {
        if (key is null || !_columns.TryGetValue(key, out var column)) return;
        if (key == SortKey) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = column.DescendingFirst;
        }
        Refresh();
    }

    /// <summary>Inserts a row at its sorted position.</summary>
    public void Add(TRow row)
    {
        var lo = 0;
        var hi = Rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Compare(Rows[mid], row) <= 0) lo = mid + 1;
            else hi = mid;
        }
        Rows.Insert(lo, row);
    }

    public void Clear() => Rows.Clear();

    /// <summary>Restores the sort order after row values changed.</summary>
    public void Refresh()
    {
        var sorted = Rows.ToList();
        sorted.Sort(Compare);
        var moves = 0;
        for (var i = 0; i < sorted.Count; i++)
            if (!ReferenceEquals(Rows[i], sorted[i])) moves++;
        if (moves == 0) return;

        if (moves > 64)
        {
            // Many moves (a re-sort of a long list): one reset is cheaper than hundreds of move events.
            Rows.Clear();
            foreach (var row in sorted) Rows.Add(row);
            return;
        }
        for (var i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(Rows[i], sorted[i])) continue;
            Rows.Move(Rows.IndexOf(sorted[i]), i);
        }
    }

    private int Compare(TRow a, TRow b)
    {
        var c = _columns[SortKey].Compare(a, b);
        if (SortDescending) c = -c;
        return c != 0 ? c : _tieBreak(a, b);
    }
}

/// <summary>Header text for x:Bind: the label with an arrow on the sort column.</summary>
public static class TableHeader
{
    public static string Label(string text, string key, string sortKey, bool descending) =>
        key == sortKey ? text + (descending ? " ▼" : " ▲") : text;
}
