using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Voiceover.Client.Services;

// Plain ObservableCollection.Clear() + a per-item Add() loop (the pattern
// used all over MainWindow for repopulating server/channel/message/friend
// lists after a reload) fires one CollectionChanged event per item, each of
// which WPF's ItemsControl has to react to individually. ReplaceAll below
// swaps the whole list and raises a single Reset notification instead -
// same visible result, one UI update instead of N+1. Only worth it because
// this is a drop-in replacement (ItemsSource bindings don't care about the
// concrete collection type, just that it implements
// INotifyCollectionChanged), not a reason to introduce it if it required
// wiring changes elsewhere.
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        // Base ObservableCollection's own Add/Clear raise these alongside
        // CollectionChanged - bypassing them via Items directly means doing
        // it manually so anything bound to Count (e.g. a badge) stays correct.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
