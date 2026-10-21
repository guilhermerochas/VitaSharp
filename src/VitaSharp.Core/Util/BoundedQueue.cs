namespace VitaSharp.Core.Util;

/// <summary>
/// Minimal bounded blocking FIFO with the <see cref="System.Collections.Concurrent.BlockingCollection{T}"/> semantics the
/// emulator uses (Add blocks while full, Take while empty, both throw <see cref="InvalidOperationException"/> once adding
/// completed and nothing is left). A bounded BlockingCollection allocates a linked cancellation source on every Add.
/// </summary>
public sealed class BoundedQueue<T>(int capacity)
{
    readonly Queue<T> _items = new(capacity);
    bool _completed;

    public int Count { get { lock (_items) return _items.Count; } }

    public void Add(T item)
    {
        lock (_items)
        {
            while (_items.Count >= capacity && !_completed) Monitor.Wait(_items);
            if (_completed) throw new InvalidOperationException("The queue has been marked as complete with regards to additions.");
            _items.Enqueue(item);
            Monitor.PulseAll(_items);
        }
    }

    public T Take()
    {
        lock (_items)
        {
            while (_items.Count == 0)
            {
                if (_completed) throw new InvalidOperationException("The queue is empty and has been marked as complete with regards to additions.");
                Monitor.Wait(_items);
            }
            var item = _items.Dequeue();
            Monitor.PulseAll(_items);
            return item;
        }
    }

    public void CompleteAdding()
    {
        lock (_items) { _completed = true; Monitor.PulseAll(_items); }
    }
}
