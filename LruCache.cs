namespace TraductorNWN
{
    // Caché LRU sencilla y thread-safe: guarda las últimas N traducciones para no
    // volver a pedir a la API frases repetidas ("hi", "lol", saludos, avisos del servidor…).
    internal sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
        private readonly object _lock = new();

        public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(_capacity, comparer);
        }

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

        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = (key, value);
                    _order.Remove(existing);
                    _order.AddFirst(existing);
                    return;
                }

                var node = _order.AddFirst((key, value));
                _map[key] = node;

                if (_map.Count > _capacity)
                {
                    var last = _order.Last!;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _order.Clear();
            }
        }
    }
}
