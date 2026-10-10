using System.Collections.Concurrent;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Events;

/// <summary>Scoped UI-thread dispatch with a queue for worker-thread publishers.</summary>
internal sealed class UiEventBus : IDisposable
{
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private readonly Action<string> _report;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly object _gate = new();
    // Copy on subscription changes, not on every publish. Reentrant handlers keep a stable snapshot.
    private readonly Dictionary<Type, Subscription[]> _subscriptions = new();
    private bool _disposed;

    internal UiEventBus(Action<string> report) => _report = report;

    internal Scope CreateScope()
    {
        lock (_gate)
        {
            EnsureOpen();
            return new Scope(this);
        }
    }

    internal void DrainPending()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
            throw new InvalidOperationException("Pending UI events must be drained on the UI thread.");
        int count = _pending.Count;
        for (int index = 0; index < count && _pending.TryDequeue(out Action? dispatch); index++)
            dispatch();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Subscription[] subscriptions in _subscriptions.Values)
            {
                foreach (Subscription subscription in subscriptions)
                {
                    subscription.IsActive = false;
                    subscription.Scope.Subscriptions.Clear();
                }
            }
            _subscriptions.Clear();
            while (_pending.TryDequeue(out _)) { }
        }
    }

    private IDisposable Subscribe<TMessage>(Scope scope, Action<TMessage> handler) where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            EnsureOpen();
            scope.EnsureOpen();
            var subscription = new Subscription<TMessage>(scope, handler);
            _subscriptions.TryGetValue(typeof(TMessage), out Subscription[]? current);
            current ??= Array.Empty<Subscription>();
            var next = new Subscription[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = subscription;
            _subscriptions[typeof(TMessage)] = next;
            scope.Subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void Publish<TMessage>(Scope scope, TMessage message) where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            EnsureOpen();
            scope.EnsureOpen();
            if (Environment.CurrentManagedThreadId != _uiThreadId)
            {
                Enqueue(scope, message);
                return;
            }
        }
        Dispatch(scope, message);
    }

    // Keep the closure off the synchronous path, including for value-type messages.
    private void Enqueue<TMessage>(Scope scope, TMessage message) where TMessage : notnull
        => _pending.Enqueue(() => Dispatch(scope, message));

    private void Dispatch<TMessage>(Scope publisher, TMessage message) where TMessage : notnull
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed || publisher.IsDisposed || !_subscriptions.TryGetValue(typeof(TMessage), out subscriptions!))
                return;
        }
        foreach (Subscription subscription in subscriptions)
        {
            if (!subscription.IsActive)
                continue;
            try
            {
                ((Subscription<TMessage>)subscription).Handler(message);
            }
            catch (Exception exception)
            {
                _report($"event handler for '{typeof(TMessage).FullName}' failed: {exception.Message}");
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (!subscription.IsActive)
                return;
            subscription.IsActive = false;
            subscription.Scope.Subscriptions.Remove(subscription);
            Subscription[] current = _subscriptions[subscription.MessageType];
            if (current.Length == 1)
            {
                _subscriptions.Remove(subscription.MessageType);
                return;
            }
            int index = Array.IndexOf(current, subscription);
            var next = new Subscription[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, next.Length - index);
            _subscriptions[subscription.MessageType] = next;
        }
    }

    private void DisposeScope(Scope scope)
    {
        lock (_gate)
        {
            if (scope.IsDisposed)
                return;
            scope.IsDisposed = true;
            while (scope.Subscriptions.Count > 0)
                Remove(scope.Subscriptions[^1]);
        }
    }

    private void EnsureOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UiEventBus));
    }

    internal sealed class Scope : IUiEventBus, IDisposable
    {
        internal readonly UiEventBus Bus;
        internal bool IsDisposed { get; set; }
        internal List<Subscription> Subscriptions { get; } = new();

        internal Scope(UiEventBus bus) => Bus = bus;

        public IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : notnull
            => Bus.Subscribe(this, handler);

        public void Publish<TMessage>(TMessage message) where TMessage : notnull
            => Bus.Publish(this, message);

        public void Dispose() => Bus.DisposeScope(this);

        internal void EnsureOpen()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(Scope));
        }
    }

    internal abstract class Subscription : IDisposable
    {
        protected Subscription(Scope scope, Type messageType)
        {
            Scope = scope;
            MessageType = messageType;
        }

        internal Scope Scope { get; }
        internal Type MessageType { get; }
        internal volatile bool IsActive = true;
        public void Dispose() => Scope.Bus.Remove(this);
    }

    private sealed class Subscription<TMessage> : Subscription where TMessage : notnull
    {
        internal Subscription(Scope scope, Action<TMessage> handler) : base(scope, typeof(TMessage))
            => Handler = handler;
        internal Action<TMessage> Handler { get; }
    }
}
