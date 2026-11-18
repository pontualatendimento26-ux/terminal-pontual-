namespace Pontual.Terminal.Ipc;

/// <summary>
/// Fila em memória dos eventos ainda não entregues ao servidor. Se o servidor ficar fora do ar por
/// muito tempo, os eventos mais antigos são descartados para a fila não crescer sem limite.
/// </summary>
public sealed class EventQueue(int capacity = 1000)
{
    private readonly object _gate = new();
    private readonly List<DeviceEvent> _items = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Enqueue(DeviceEvent item)
    {
        lock (_gate)
        {
            _items.Add(item);
            if (_items.Count > capacity)
            {
                _items.RemoveRange(0, _items.Count - capacity);
            }
        }
    }

    /// <summary>Os primeiros eventos da fila, sem removê-los.</summary>
    public IReadOnlyList<DeviceEvent> Peek(int max)
    {
        lock (_gate)
        {
            return _items.Take(max).ToArray();
        }
    }

    /// <summary>Remove os eventos de um lote já entregue (ou recusado de vez pelo servidor).</summary>
    public void Acknowledge(IReadOnlyList<DeviceEvent> batch)
    {
        lock (_gate)
        {
            foreach (var item in batch)
            {
                var index = _items.FindIndex(i => ReferenceEquals(i, item));
                if (index >= 0)
                {
                    _items.RemoveAt(index);
                }
            }
        }
    }
}
