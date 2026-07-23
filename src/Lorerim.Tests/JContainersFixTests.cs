using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;
using Lorerim.Gui.Services.Modlist;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The Nexus builds of JContainers SE crash under Proton; a patched build has to be swapped
/// in after the engine finishes. These cover the detection half — deciding which DLLs are
/// stale — which is what gates the download-and-replace.
/// </summary>
public class JContainersFixTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lorerim-jc-test").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup trouble must not turn into a spurious test failure.
        }
    }

    [Fact]
    public void ModlistWithoutJContainersNeedsNoFix()
    {
        WriteSkse(JContainersFix.TargetSkseDll);

        Assert.Empty(JContainersFix.FindOutdated(_root, AnyHash));
    }

    [Fact]
    public void MissingModsDirectoryNeedsNoFix()
    {
        Assert.Empty(JContainersFix.FindOutdated(Path.Join(_root, "nonexistent"), AnyHash));
    }

    [Fact]
    public void UntestedSkseVersionIsLeftAlone()
    {
        // The patched DLL is built against one SKSE runtime. Against any other, replacing
        // the DLL would turn a working install into a broken one.
        WriteSkse("skse64_1_5_97.dll");
        WriteJContainers("JContainers SE", "nexus build");

        Assert.Empty(JContainersFix.FindOutdated(_root, AnyHash));
    }

    [Fact]
    public void NexusBuildIsReportedAsOutdated()
    {
        WriteSkse(JContainersFix.TargetSkseDll);
        var dll = WriteJContainers("JContainers SE", "nexus build");

        Assert.Equal([dll], JContainersFix.FindOutdated(_root, AnyHash));
    }

    [Fact]
    public void AlreadyPatchedBuildIsNotReportedAgain()
    {
        WriteSkse(JContainersFix.TargetSkseDll);
        var dll = WriteJContainers("JContainers SE", "patched build");

        Assert.Empty(JContainersFix.FindOutdated(_root, JContainersFix.Sha256Hex(dll)));
    }

    [Fact]
    public void EveryStaleCopyIsReported()
    {
        // Modlists can ship the DLL in more than one mod folder; a half-applied fix still crashes.
        WriteSkse(JContainersFix.TargetSkseDll);
        WriteJContainers("JContainers SE", "nexus build");
        WriteJContainers("JContainers SE - Patch", "nexus build");

        Assert.Equal(2, JContainersFix.FindOutdated(_root, AnyHash).Count);
    }

    [Fact]
    public void PatchedCopiesAreSkippedWhileStaleOnesAreReported()
    {
        WriteSkse(JContainersFix.TargetSkseDll);
        var good = WriteJContainers("JContainers SE", "patched build");
        var stale = WriteJContainers("JContainers SE - Patch", "nexus build");

        Assert.Equal([stale], JContainersFix.FindOutdated(_root, JContainersFix.Sha256Hex(good)));
    }

    [Theory]
    [InlineData("SKSE", "Plugins")]
    [InlineData("skse", "plugins")]
    [InlineData("Skse", "PlugIns")]
    public void PluginFoldersAreFoundWhateverTheirCase(string skse, string plugins)
    {
        // Mod archives are authored on Windows, where case does not matter. Linux takes the
        // extracted casing literally, so matching "SKSE/Plugins" exactly misses real mods —
        // and a missed DLL is the silent crash this whole fix exists to prevent.
        WriteSkse(JContainersFix.TargetSkseDll);
        var dir = Path.Join(_root, "mods", "JContainers SE", skse, plugins);
        Directory.CreateDirectory(dir);
        var dll = Path.Join(dir, "JContainers64.dll");
        File.WriteAllText(dll, "nexus build");

        Assert.Equal([dll], JContainersFix.FindOutdated(_root, AnyHash));
    }

    [Theory]
    [InlineData("Root", "skse64_1_6_1170.dll")]
    [InlineData("root", "SKSE64_1_6_1170.dll")]
    public void TheSkseRuntimeIsRecognisedWhateverItsCase(string root, string dllName)
    {
        var dir = Path.Join(_root, "mods", "Skyrim Script Extender (SKSE64)", root);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, dllName), "skse");

        Assert.True(JContainersFix.TargetsSupportedSkse(_root));
    }

    [Fact]
    public void AStrayLoaderInAnEarlierModDoesNotSuppressTheFix()
    {
        // Asking "which loader did I stumble on first" across ~4000 mod folders lets one
        // stray file switch the fix off; the question is whether the target runtime is present.
        var stray = Path.Join(_root, "mods", "AAA Compat Mod", "Root");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Join(stray, "skse64_1_5_97.dll"), "old loader");
        WriteSkse(JContainersFix.TargetSkseDll);

        Assert.True(JContainersFix.TargetsSupportedSkse(_root));
    }

    [Fact]
    public void AModlistOnADifferentSkseRuntimeIsNotRecognised()
    {
        WriteSkse("skse64_1_5_97.dll");

        Assert.False(JContainersFix.TargetsSupportedSkse(_root));
    }

    [Fact]
    public void TheSkseRuntimeIsFoundWhenItShipsInTheGameFolderInstead()
    {
        // Some modlists put the loader in "Stock Game"/"Game Root" rather than a mod folder.
        var dir = Path.Join(_root, "Stock Game");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, JContainersFix.TargetSkseDll), "skse");

        Assert.True(JContainersFix.TargetsSupportedSkse(_root));
    }

    [Theory]
    [InlineData("-w/tmp/evil/JContainers64.dll")]
    [InlineData("@/etc/passwd/JContainers64.dll")]
    [InlineData("../../../etc/JContainers64.dll")]
    [InlineData("/absolute/JContainers64.dll")]
    [InlineData("glob*/JContainers64.dll")]
    public void ArchiveEntriesThatWouldBeReadAsSwitchesOrEscapesAreRefused(string entry)
    {
        // The entry name is handed to 7-Zip as an argument; one starting with "-" is taken as
        // a switch, which changes what gets extracted.
        Assert.Null(JContainersFix.DllPathInArchive($"Path = {entry}\nAttributes = A_"));
    }

    [Fact]
    public void ADirectoryEntryIsNotMistakenForTheDll()
    {
        // A directory named JContainers64.dll sorts ahead of the real file and would extract
        // as a directory, leaving a confusing "was not extracted" failure.
        const string listing = """
            Path = decoy/JContainers64.dll
            Attributes = D_ drwxr-xr-x

            Path = SKSE/Plugins/JContainers64.dll
            Attributes = A_ -rw-r--r--
            """;

        Assert.Equal("SKSE/Plugins/JContainers64.dll", JContainersFix.DllPathInArchive(listing));
    }

    [Fact]
    public void ArchiveListingYieldsTheDllEntryPath()
    {
        // `7zz l -ba -slt` output; we take the DLL's internal path to extract just that member.
        const string listing = """
            Path = SKSE/Plugins/JContainers64.dll
            Size = 2993152

            Path = readme.txt
            Size = 12
            """;

        Assert.Equal("SKSE/Plugins/JContainers64.dll", JContainersFix.DllPathInArchive(listing));
    }

    [Fact]
    public void ArchiveWithoutTheDllYieldsNothing()
    {
        Assert.Null(JContainersFix.DllPathInArchive("Path = readme.txt\nSize = 12"));
    }

    [Fact]
    public void PinnedDownloadUrlIsAVerifiableHttpsAsset()
    {
        // A plain-HTTP or non-GitHub origin would mean the pinned hash guards nothing useful.
        Assert.StartsWith(
            "https://github.com/rfortier/JContainers-rwf/releases/",
            JContainersFix.DownloadUrl
        );
        Assert.Equal(64, JContainersFix.FixedSha256.Length);
    }

    [Fact]
    public void ExtractorIsTakenFromTheBundledEngine()
    {
        var engineDir = Path.Join(_root, "engine");
        var extractor = Path.Join(engineDir, "Extractors", "linux-x64", "7zz");
        Directory.CreateDirectory(Path.GetDirectoryName(extractor)!);
        File.WriteAllText(extractor, "");

        Assert.Equal(extractor, JContainersFix.ExtractorPath(engineDir));
    }

    [Fact]
    public void MissingExtractorYieldsNothingRatherThanAGuess()
    {
        Assert.Null(JContainersFix.ExtractorPath(Path.Join(_root, "engine")));
        Assert.Null(JContainersFix.ExtractorPath(null));
    }

    [Fact]
    public void ReplacementSwapsTheDllAndKeepsTheOriginal()
    {
        WriteSkse(JContainersFix.TargetSkseDll);
        var target = WriteJContainers("JContainers SE", "nexus build");
        var patched = PatchedSource("patched build");

        JContainersFix.Replace(target, patched, JContainersFix.Sha256Hex(patched));

        Assert.Equal("patched build", File.ReadAllText(target));
        Assert.Equal("nexus build", File.ReadAllText(target + JContainersFix.BackupSuffix));
    }

    [Fact]
    public void RepeatedReplacementKeepsTheOriginalBackupIntact()
    {
        // Overwriting the backup on a second run would leave no way back to the shipped DLL.
        WriteSkse(JContainersFix.TargetSkseDll);
        var target = WriteJContainers("JContainers SE", "nexus build");
        var patched = PatchedSource("patched build");
        var hash = JContainersFix.Sha256Hex(patched);

        JContainersFix.Replace(target, patched, hash);
        JContainersFix.Replace(target, patched, hash);

        Assert.Equal("nexus build", File.ReadAllText(target + JContainersFix.BackupSuffix));
    }

    [Fact]
    public void ReplacementIsRefusedWhenTheSourceFailsItsHashCheck()
    {
        // A tampered or truncated download must never reach the game directory.
        WriteSkse(JContainersFix.TargetSkseDll);
        var target = WriteJContainers("JContainers SE", "nexus build");
        var patched = PatchedSource("not what we asked for");

        Assert.Throws<InvalidOperationException>(
            () => JContainersFix.Replace(target, patched, JContainersFix.FixedSha256)
        );
        Assert.Equal("nexus build", File.ReadAllText(target));
        Assert.False(File.Exists(target + JContainersFix.BackupSuffix));
    }

    [Fact]
    public async Task FixupServiceDoesNothingForAModlistWithoutJContainers()
    {
        // No network and no engine needed to decide there is nothing to do — the install
        // pipeline runs this unconditionally, so the no-op path must stay free of both.
        WriteSkse(JContainersFix.TargetSkseDll);
        var service = new ModFixupService(new UnusableHttpClientFactory(), new JackifyEngineLocator(), new LogService(null));

        Assert.Equal(0, await service.ApplyJContainersFixAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task FixupPassWithoutAnExtractorFailsInsteadOfSkippingQuietly()
    {
        // A skipped fix means the modlist crashes on launch with nothing pointing at the
        // cause, so an install that needs the fix and cannot apply it has to say so.
        WriteSkse(JContainersFix.TargetSkseDll);
        var dll = WriteJContainers("JContainers SE", "nexus build");
        var service = new ModFixupService(new UnusableHttpClientFactory(), new NoEngineLocator(), new LogService(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(_root, CancellationToken.None)
        );

        Assert.Contains("crash on launch", error.Message);
        Assert.Equal("nexus build", File.ReadAllText(dll));
        Assert.False(File.Exists(dll + JContainersFix.BackupSuffix));
    }

    [Fact]
    public async Task AnArchiveThatFailsItsChecksumIsNeverExtracted()
    {
        // GitHub release assets can be replaced under the same URL, so the archive is checked
        // before any of its bytes reach the extractor — not just the DLL that comes out.
        var service = new ModFixupService(
            new FixedBytesHttpClientFactory("not the real archive"u8.ToArray()),
            new NoEngineLocator(),
            new LogService(null)
        );

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadVerifiedAsync(Path.Join(_root, "a.7z"), 1 << 20, CancellationToken.None)
        );

        Assert.Contains("checksum", error.Message);
    }

    /// <summary>Serves a fixed body, standing in for a replaced or corrupted release asset.</summary>
    private sealed class FixedBytesHttpClientFactory(byte[] body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixedHandler(body));

        private sealed class FixedHandler(byte[] body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                });
        }
    }

    /// <summary>Resolves no engine regardless of LORERIM_ENGINE_PATH or the host layout.</summary>
    private sealed class NoEngineLocator : JackifyEngineLocator
    {
        public override string? EngineDir => null;
    }

    [Fact]
    public async Task AnEndlessDownloadIsAbandonedInsteadOfFillingTheDisk()
    {
        // The archive is ~1 MB. A server that never stops sending must not be allowed to
        // write until the temp filesystem is full.
        var service = new ModFixupService(new EndlessHttpClientFactory(), new NoEngineLocator(), new LogService(null));
        var destination = Path.Join(_root, "archive.7z");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAsync(destination, maxBytes: 4096, CancellationToken.None)
        );

        Assert.True(new FileInfo(destination).Length <= 4096 + 65536);
    }

    /// <summary>Streams bytes forever, like a server that never closes the response.</summary>
    private sealed class EndlessHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new EndlessHandler());

        private sealed class EndlessHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EndlessStream()),
                });
        }

        private sealed class EndlessStream : Stream
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                buffer.AsSpan(offset, count).Clear();
                return count;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    /// <summary>Hands out a client whose use would fail the test, proving the no-op path stays offline.</summary>
    private sealed class UnusableHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new UnusableHandler()) { BaseAddress = new Uri("http://localhost.invalid") };

        private sealed class UnusableHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                throw new InvalidOperationException("the no-op path must not reach the network");
        }
    }

    /// <summary>A hash no generated fixture will ever match, so every DLL counts as stale.</summary>
    private const string AnyHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private string PatchedSource(string contents)
    {
        var path = Path.Join(_root, "staging", "JContainers64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private void WriteSkse(string dllName)
    {
        var dir = Path.Join(_root, "mods", "Skyrim Script Extender (SKSE64)", "Root");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, dllName), "skse");
    }

    private string WriteJContainers(string modName, string contents)
    {
        var dir = Path.Join(_root, "mods", modName, "SKSE", "Plugins");
        Directory.CreateDirectory(dir);
        var path = Path.Join(dir, "JContainers64.dll");
        File.WriteAllText(path, contents);
        return path;
    }
}
