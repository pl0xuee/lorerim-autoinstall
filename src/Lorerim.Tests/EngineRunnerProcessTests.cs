using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Runs the real runner against a stand-in engine script, so what is verified is the wiring —
/// stderr read live, exit code read, structured error turned into the failure message — not
/// just the parser in isolation.
/// </summary>
public class EngineRunnerProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lorerim-fake-engine").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup trouble must not turn into a spurious test failure.
        }
    }

    private sealed class FixedLocator(string dir) : JackifyEngineLocator
    {
        public override string? EngineDir => dir;
    }

    private (JackifyEngineRunner Runner, List<string> Log) Runner(string script)
    {
        var path = Path.Join(_dir, JackifyEngineLocator.BinaryName);
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var log = new LogService(null);
        var lines = new List<string>();
        log.LineAdded += line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        };
        return (new JackifyEngineRunner(new FixedLocator(_dir), new EngineProgress(), log), lines);
    }

    private static EngineInstallRequest Request() =>
        new("list.wabbajack", null, "/tmp/out", "/tmp/dl", new Dictionary<string, string>());

    [Fact]
    public async Task AStructuredErrorOnStderrBecomesTheFailureMessage()
    {
        var (runner, log) = Runner(
            """
            echo '[FILE_PROGRESS] Downloading: a.7z (10%) (1/2)'
            echo '{"je":"1","level":"error","type":"disk_full","message":"Disk is full."}' >&2
            exit 4
            """
        );

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            runner.InstallAsync(Request(), CancellationToken.None)
        );

        Assert.StartsWith("Disk full: Disk is full.", ex.Message);
        lock (log)
        {
            // The raw line is still logged: it is what a bug report needs.
            Assert.Contains(log, l => l.Contains("\"type\":\"disk_full\""));
        }
    }

    [Fact]
    public async Task AnExitCodeAloneStillNamesTheFailure()
    {
        var (runner, _) = Runner("exit 2");

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            runner.InstallAsync(Request(), CancellationToken.None)
        );

        Assert.StartsWith("Nexus sign-in failed", ex.Message);
    }

    [Fact]
    public async Task AnUnnamedFailureKeepsTheGenericMessage()
    {
        var (runner, _) = Runner("echo 'something odd' >&2\nexit 6");

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            runner.InstallAsync(Request(), CancellationToken.None)
        );

        Assert.Contains("exited with code 6", ex.Message);
    }

    /// <summary>
    /// stderr used to be held back until stdout closed, so a warning from the first hour of a
    /// long install reached the log only when the whole run ended.
    /// </summary>
    [Fact]
    public async Task StderrReachesTheLogWhileTheEngineIsStillRunning()
    {
        var marker = Path.Join(_dir, "go");
        var (runner, log) = Runner(
            $$"""
            echo 'early warning' >&2
            while [ ! -f '{{marker}}' ]; do sleep 0.05; done
            exit 0
            """
        );

        var run = runner.InstallAsync(Request(), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var seenWhileRunning = false;
        while (DateTime.UtcNow < deadline)
        {
            lock (log)
            {
                seenWhileRunning = log.Exists(l => l.Contains("early warning"));
            }
            if (seenWhileRunning)
            {
                break;
            }
            await Task.Delay(50);
        }
        var stillRunning = !run.IsCompleted;
        File.WriteAllText(marker, "");
        await run;

        Assert.True(seenWhileRunning, "stderr line never reached the log while the engine ran");
        Assert.True(stillRunning, "the engine had already exited, so this proved nothing");
    }
}
