using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Lorerim.Gui.Services;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Regression guard for the OAuth freeze: the token store used to persist with
/// WriteAllTextAsync(...).GetAwaiter().GetResult(). On the UI thread that deadlocks — the
/// async write queues its continuation to the very thread blocked in GetResult(). Writing
/// from a synchronous caller must therefore use the synchronous path.
/// </summary>
public class AtomicFileContextTests
{
    /// <summary>A UI-like context: continuations queue to one thread and run only when it pumps.</summary>
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue =
            new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    }

    [Fact]
    public void SynchronousWriteCompletesOnAContextThatNeverPumps()
    {
        var path = Path.Join(Path.GetTempPath(), $"lorerim-atomic-{Guid.NewGuid():N}.json");
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
            try
            {
                AtomicFile.WriteAllText(path, "{\"access_token\":\"x\"}");
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();

        // The old sync-over-async write never returns here; 10s is generous for a small file.
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "write deadlocked under a UI-like context");
        Assert.Null(failure);
        Assert.Equal("{\"access_token\":\"x\"}", File.ReadAllText(path));

        File.Delete(path);
    }
}
