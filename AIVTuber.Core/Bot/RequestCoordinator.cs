using System.Threading.Channels;

namespace AIVTuber.Core.Bot;

internal enum InputSource
{
    Loopback,
    Danmaku,
    Microphone,
    Manual,
}

internal readonly record struct RequestGeneration(long Value);

internal readonly record struct InputEnvelope(
    InputSource Source,
    int Priority,
    long Timestamp,
    long Sequence,
    RequestGeneration Generation);

internal interface IMonotonicClock
{
    long GetTimestamp();
}

internal sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
}

/// <summary>
/// Capacity-one, latest-wins request coordinator. A newer accepted request invalidates the
/// current generation immediately, cancels active work, and replaces queued work.
/// </summary>
internal sealed class RequestCoordinator : IAsyncDisposable
{
    private sealed record QueuedRequest(
        InputEnvelope Envelope,
        Func<InputEnvelope, CancellationToken, Task> Execute,
        CancellationToken CallerToken,
        TaskCompletionSource<bool> Completion);

    internal const int Capacity = 1;

    private readonly object _sync = new();
    private readonly Channel<QueuedRequest> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IMonotonicClock _clock;
    private readonly Task _consumerTask;
    private QueuedRequest? _pending;
    private CancellationTokenSource? _activeCts;
    private Task _activeTask = Task.CompletedTask;
    private long _sequence;
    private long _currentGeneration;
    private bool _disposed;

    public RequestCoordinator(IMonotonicClock? clock = null)
    {
        _clock = clock ?? new StopwatchMonotonicClock();
        _channel = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        _consumerTask = ConsumeAsync();
    }

    public long CurrentGeneration => Interlocked.Read(ref _currentGeneration);

    public bool IsBusy
    {
        get
        {
            lock (_sync) return _pending is not null || !_activeTask.IsCompleted;
        }
    }

    public static int PriorityOf(InputSource source) => source switch
    {
        InputSource.Loopback => 0,
        InputSource.Danmaku => 1,
        InputSource.Microphone => 2,
        InputSource.Manual => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown input source."),
    };

    public bool IsCurrent(RequestGeneration generation) =>
        generation.Value != 0 && generation.Value == CurrentGeneration;

    public Task<bool> EnqueueAsync(
        InputSource source,
        Func<InputEnvelope, CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);

        QueuedRequest request;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (source == InputSource.Loopback && (_pending is not null || !_activeTask.IsCompleted))
                return Task.FromResult(false);

            var sequence = ++_sequence;
            var generation = new RequestGeneration(sequence);
            var envelope = new InputEnvelope(
                source,
                PriorityOf(source),
                _clock.GetTimestamp(),
                sequence,
                generation);
            request = new QueuedRequest(
                envelope,
                execute,
                cancellationToken,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

            Interlocked.Exchange(ref _currentGeneration, generation.Value);
            _activeCts?.Cancel();
            _pending?.Completion.TrySetResult(false);
            _pending = request;

            if (!_channel.Writer.TryWrite(request))
            {
                _pending = null;
                request.Completion.TrySetException(new InvalidOperationException("Request coordinator is stopped."));
            }
        }

        return request.Completion.Task;
    }

    public async Task CancelCurrentAsync()
    {
        Task activeTask;
        lock (_sync)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _currentGeneration);
            _activeCts?.Cancel();
            _pending?.Completion.TrySetResult(false);
            _pending = null;
            activeTask = _activeTask;
        }

        try { await activeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                CancellationTokenSource? requestCts = null;
                Task executionTask;
                lock (_sync)
                {
                    if (!ReferenceEquals(_pending, request) || !IsCurrent(request.Envelope.Generation))
                    {
                        request.Completion.TrySetResult(false);
                        continue;
                    }

                    _pending = null;
                    requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token, request.CallerToken);
                    _activeCts = requestCts;
                    executionTask = ExecuteRequestAsync(request, requestCts.Token);
                    _activeTask = executionTask;
                }

                try { await executionTask.ConfigureAwait(false); }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeCts, requestCts)) _activeCts = null;
                    }
                    requestCts.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ExecuteRequestAsync(QueuedRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(request.Envelope.Generation))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            await request.Execute(request.Envelope, cancellationToken).ConfigureAwait(false);
            request.Completion.TrySetResult(IsCurrent(request.Envelope.Generation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            request.Completion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            request.Completion.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task activeTask;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _currentGeneration);
            _activeCts?.Cancel();
            _pending?.Completion.TrySetResult(false);
            _pending = null;
            activeTask = _activeTask;
            _channel.Writer.TryComplete();
            _shutdown.Cancel();
        }

        try { await activeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try { await _consumerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
