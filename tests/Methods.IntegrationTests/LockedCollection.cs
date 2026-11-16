using System.Collections;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     A collection that may be written from other threads while a test reads it, e.g. the target of the OpenTelemetry
///     in-memory exporter (spans are added on the thread that ends them). Read it through <see cref="Snapshot" />.
/// </summary>
internal sealed class LockedCollection<T> : ICollection<T>
{
    private readonly List<T> _items = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    /// <summary>A copy of the items, taken under the lock.</summary>
    public T[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public bool Contains(T item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(T item)
    {
        lock (_lock)
        {
            return _items.Remove(item);
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
