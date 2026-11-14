using System.Collections;

namespace Perch.Pages.SystemMonitor;

/// <summary>Fixed-capacity history. When full, each Add drops the oldest item. Index 0 is the oldest.</summary>
public sealed class RingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private int _next;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;

    public void Add(T item)
    {
        _items[_next] = item;
        _next = (_next + 1) % _items.Length;
        if (_count < _items.Length) _count++;
    }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            var start = (_next - _count + _items.Length) % _items.Length;
            return _items[(start + index) % _items.Length];
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _next = 0;
        _count = 0;
    }

    public T[] ToArray()
    {
        var result = new T[_count];
        for (var i = 0; i < _count; i++) result[i] = this[i];
        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
