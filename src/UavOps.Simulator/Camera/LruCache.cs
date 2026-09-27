namespace UavOps.Simulator.Camera;

/// <summary>A small thread-safe least-recently-used cache.</summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly object _lock = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            _map[key] = _order.AddFirst((key, value));
            while (_map.Count > capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                // Not disposed: a renderer on another thread may still be drawing it. SKImage
                // frees its pixels when collected.
            }
        }
    }
}
