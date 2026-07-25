using System;
using System.IO;
using Lorerim.Gui.Services;
using Xunit;

namespace Lorerim.Tests;

public class LogServiceTests
{
    [Fact]
    public void AMemoryOnlySinkHasNoFileToOffer()
    {
        // The UI offers "Open log file" from this path; a null must not become a broken button.
        Assert.Null(new LogService(logDirectory: null).LogFilePath);
    }

    [Fact]
    public void TheLogFilePathPointsAtTheFileLinesAreWrittenTo()
    {
        var dir = Directory.CreateTempSubdirectory("lorerim-log-test");
        try
        {
            var log = new LogService(dir.FullName);

            log.Append("hello");

            Assert.NotNull(log.LogFilePath);
            Assert.True(File.Exists(log.LogFilePath));
            Assert.Contains("hello", File.ReadAllText(log.LogFilePath!));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheFileLineCarriesTheSameClockReadingAsThePaneLine()
    {
        // Two DateTime.Now reads can straddle midnight and stamp the file with a different
        // day than the time shown beside it in the pane.
        var dir = Directory.CreateTempSubdirectory("lorerim-log-test");
        try
        {
            var log = new LogService(dir.FullName);
            string? fromPane = null;
            log.LineAdded += line => fromPane = line;

            log.Append("engine started");

            var fromFile = File.ReadAllText(log.LogFilePath!).TrimEnd('\n');
            Assert.NotNull(fromPane);
            Assert.EndsWith(fromPane, fromFile, StringComparison.Ordinal);
            Assert.StartsWith($"[{DateTime.Now:yyyy-MM-dd}]", fromFile, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
