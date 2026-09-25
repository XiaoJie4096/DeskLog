namespace Riji.Infrastructure;

// Fairly schedules text requests so one summary cannot occupy every provider slot.
internal sealed class TextRequestScheduler
{
    private const int MaxConcurrent = 5;
    private const int MaxPerOwner = 4;
    private readonly object gate = new();
    private readonly List<Request> waiting = [];
    private readonly Dictionary<string, int> activeByOwner = [];
    private long sequence;
    private int active;
    private int activeHourly;
    private bool closing;

    private sealed class Request
    {
        public required string Owner;
        public required bool Hourly;
        public required Func<CancellationToken, Task<string>> Operation;
        public required CancellationToken Cancellation;
        public required TaskCompletionSource<string> Completion;
        public long Sequence;
        public CancellationTokenRegistration Registration;
    }

    public Task<string> Run(string owner, bool hourly, Func<CancellationToken, Task<string>> operation, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<string>(cancellation);
        Request request;
        lock (gate)
        {
            if (closing) return Task.FromException<string>(new InvalidOperationException("文本任务调度器正在关闭。"));
            request = new Request { Owner = owner, Hourly = hourly, Operation = operation, Cancellation = cancellation, Completion = completion, Sequence = sequence++ };
            waiting.Add(request);
            request.Registration = cancellation.Register(() => Cancel(request));
            _ = completion.Task.ContinueWith(_ => request.Registration.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            PumpLocked();
        }
        return completion.Task;
    }

    private void Cancel(Request request)
    {
        lock (gate)
        {
            if (waiting.Remove(request))
            {
                request.Completion.TrySetCanceled(request.Cancellation);
                PumpLocked();
            }
        }
    }

    private void PumpLocked()
    {
        while (!closing && active < MaxConcurrent)
        {
            var index = FindNextLocked();
            if (index < 0) return;
            var request = waiting[index];
            waiting.RemoveAt(index);
            active++;
            if (request.Hourly) activeHourly++;
            activeByOwner[request.Owner] = activeByOwner.GetValueOrDefault(request.Owner) + 1;
            _ = Execute(request);
        }
    }

    private int FindNextLocked()
    {
        var candidates = waiting.Select((request, index) => (request, index))
            .Where(item => activeByOwner.GetValueOrDefault(item.request.Owner) < MaxPerOwner);
        var hourly = candidates.Where(item => item.request.Hourly).OrderBy(item => item.request.Sequence).FirstOrDefault();
        var other = candidates.Where(item => !item.request.Hourly).OrderBy(item => item.request.Sequence).FirstOrDefault();
        if (hourly.request is not null && (activeHourly == 0 || other.request is null)) return hourly.index;
        return other.request?.Sequence is not null ? other.index : -1;
    }

    private async Task Execute(Request request)
    {
        try
        {
            var result = await request.Operation(request.Cancellation);
            request.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(request.Cancellation);
        }
        catch (Exception error) { request.Completion.TrySetException(error); }
        finally
        {
            request.Registration.Dispose();
            lock (gate)
            {
                active--;
                if (request.Hourly) activeHourly--;
                var remaining = activeByOwner[request.Owner] - 1;
                if (remaining == 0) activeByOwner.Remove(request.Owner);
                else activeByOwner[request.Owner] = remaining;
                PumpLocked();
            }
        }
    }

    public void Shutdown()
    {
        lock (gate)
        {
            closing = true;
            foreach (var request in waiting)
            {
                request.Completion.TrySetException(new InvalidOperationException("文本任务调度器正在关闭。"));
            }
            waiting.Clear();
        }
    }
}
