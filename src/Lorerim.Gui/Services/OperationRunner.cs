using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Lorerim.Gui.Services;

public enum OperationOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

public sealed record OperationResult(OperationOutcome Outcome, Exception? Error = null);

/// <summary>
/// Runs one operation at a time (a single jackify-engine process plus Steam integration must
/// never run concurrently with another install). Each run gets a fresh DI scope and a
/// per-operation CancellationTokenSource.
/// </summary>
public class OperationRunner(IServiceProvider serviceProvider, LogService log)
{
    public bool IsBusy => _busy == 1;
    public string? CurrentOperation { get; private set; }

    public event Action<string>? Started;
    public event Action<string, OperationResult>? Completed;

    public async Task<OperationResult> RunAsync(
        string name,
        Func<IServiceProvider, CancellationToken, Task> work
    )
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new OperationResult(
                OperationOutcome.Failed,
                new InvalidOperationException($"Another operation is running: {CurrentOperation}")
            );
        }

        CurrentOperation = name;
        _cts = new CancellationTokenSource();
        Started?.Invoke(name);
        log.Append($"{name}: started");
        OperationResult result;
        try
        {
            using var scope = serviceProvider.CreateScope();
            await Task.Run(() => work(scope.ServiceProvider, _cts.Token), _cts.Token);
            result = new OperationResult(OperationOutcome.Succeeded);
            log.Append($"{name}: finished");
        }
        catch (OperationCanceledException)
        {
            result = new OperationResult(OperationOutcome.Cancelled);
            log.Append($"{name}: cancelled");
        }
        catch (Exception) when (_cts?.IsCancellationRequested == true)
        {
            // Subprocess failures surface as arbitrary exception types when we kill the
            // engine on cancel, so go by the token, not the exception.
            result = new OperationResult(OperationOutcome.Cancelled);
            log.Append($"{name}: cancelled");
        }
        catch (Exception e)
        {
            result = new OperationResult(OperationOutcome.Failed, e);
            log.Append($"{name}: FAILED — {e.Message}");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CurrentOperation = null;
            Interlocked.Exchange(ref _busy, 0);
        }
        Completed?.Invoke(name, result);
        return result;
    }

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced the finally block disposing the CTS as the run wound down; the operation
            // is over either way, so a late Cancel click is a no-op.
        }
    }

    private int _busy;
    private CancellationTokenSource? _cts;
}
