using System;
using System.IO;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Whether a Steam Linux Runtime is installed decides whether a Proton build can run at all.
/// A half-downloaded runtime counts as absent — Steam reports that through StateFlags.
/// </summary>
public class SteamRuntimeCatalogTests : IDisposable
{
    private const int Sniper = 1628350;

    private readonly string _root = Directory
        .CreateTempSubdirectory("lorerim-runtime-test")
        .FullName;

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
    public void FullyInstalledRuntimeIsAvailable()
    {
        WriteAppManifest(_root, Sniper, stateFlags: 4);

        Assert.True(Availability()(Sniper));
    }

    [Fact]
    public void MissingRuntimeIsNotAvailable()
    {
        Assert.False(Availability()(Sniper));
    }

    [Fact]
    public void PartiallyDownloadedRuntimeIsNotAvailable()
    {
        // StateFlags 1026 = update started but not complete. Launching against it fails.
        WriteAppManifest(_root, Sniper, stateFlags: 1026);

        Assert.False(Availability()(Sniper));
    }

    [Fact]
    public void RuntimeInASecondLibraryIsFound()
    {
        var library = Path.Join(_root, "elsewhere");
        WriteAppManifest(library, Sniper, stateFlags: 4);
        WriteLibraryFolders(library);

        Assert.True(Availability()(Sniper));
    }

    [Fact]
    public void EverythingIsAvailableWhenSteamCannotBeLocated()
    {
        // Without a Steam root the app cannot know what is installed, so it must not filter
        // out Proton builds that may well work.
        Assert.True(new SteamRuntimeCatalog().AvailabilityFor(null)(Sniper));
    }

    private Func<int, bool> Availability() => new SteamRuntimeCatalog().AvailabilityFor(_root);

    private static void WriteAppManifest(string libraryRoot, int appId, int stateFlags)
    {
        var steamapps = Path.Join(libraryRoot, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(
            Path.Join(steamapps, $"appmanifest_{appId}.acf"),
            $$"""
            "AppState"
            {
              "appid"  "{{appId}}"
              "StateFlags"  "{{stateFlags}}"
              "installdir"  "SteamLinuxRuntime_sniper"
            }
            """
        );
    }

    private void WriteLibraryFolders(string extraLibrary)
    {
        var steamapps = Path.Join(_root, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(
            Path.Join(steamapps, "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
              "0"
              {
                "path"  "{{_root}}"
              }
              "1"
              {
                "path"  "{{extraLibrary}}"
              }
            }
            """
        );
    }
}
