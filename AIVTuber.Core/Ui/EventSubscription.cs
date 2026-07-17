namespace AIVTuber.Core.Ui;

/// <summary>
/// Tracks a single event source so repeated lifecycle notifications do not duplicate handlers.
/// </summary>
public sealed class EventSubscription<T> where T : class
{
    private T? _source;

    public void Reconcile(T? source, Action<T> subscribe, Action<T> unsubscribe)
    {
        if (ReferenceEquals(_source, source))
            return;

        Clear(unsubscribe);
        if (source is null)
            return;

        subscribe(source);
        _source = source;
    }

    public void Clear(Action<T> unsubscribe)
    {
        if (_source is null)
            return;

        unsubscribe(_source);
        _source = null;
    }
}
