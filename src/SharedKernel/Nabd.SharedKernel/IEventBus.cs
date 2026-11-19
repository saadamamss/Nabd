using System.Collections.Concurrent;

namespace Nabd.SharedKernel;

/// <summary>
/// P4 spec 04 / ADR-006 (D4): in-process events on plain channels — no MediatR,
/// no broker, no serialization. A publisher awaits its subscribers (order per
/// device is a law of the engine), but a subscriber's failure NEVER travels
/// back into the publisher: the pump has already committed, and an alert-side
/// bug must not un-commit a stored reading.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : class;
    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class;
}

public sealed class InProcessEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public async Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : class
    {
        Delegate[] handlers;
        lock (_gate)
            handlers = _handlers.TryGetValue(typeof(T), out var list) ? list.ToArray() : [];

        foreach (var handler in handlers)
        {
            try
            {
                await ((Func<T, CancellationToken, Task>)handler)(evt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // a real shutdown, not a subscriber's bug
            }
            catch
            {
                // swallowed BY LAW (see the interface): the durable outcome already
                // happened; a broken subscriber is logged by its own room, not here.
            }
        }
    }

    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : class
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = [];
            list.Add(handler);
        }
        return new Subscription(() => { lock (_gate) _handlers[typeof(T)].Remove(handler); });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
