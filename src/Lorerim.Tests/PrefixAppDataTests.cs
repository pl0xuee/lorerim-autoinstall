using System;
using System.IO;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// MO2 redirects the game's plugins.txt onto AppData/Local/Skyrim Special Edition. A freshly
/// created Proton prefix has no such folder — Skyrim itself normally makes it, and Skyrim runs
/// under its own appid in a different prefix — so MO2 establishes no redirect and the game
/// generates a default load order with every plugin disabled. Observed 2026-07-23: the game
/// launched with 0 of 3412 plugins enabled and blamed an innocent mod for being missing.
/// </summary>
public class PrefixAppDataTests : IDisposable
{
    private readonly string _pfx = Directory.CreateTempSubdirectory("lorerim-pfx-test").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pfx, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup trouble must not turn into a spurious test failure.
        }
    }

    [Fact]
    public void CreatesTheFolderForTheSteamUser()
    {
        WriteUser("steamuser");

        Assert.Equal(1, ProtonPrefixService.EnsureSkyrimLocalAppData(_pfx));
        Assert.True(Directory.Exists(GameDir("steamuser")));
    }

    [Fact]
    public void CoversAPrefixWhoseUserIsNotCalledSteamuser()
    {
        // Wine prefixes not made by Proton use the Unix account name instead.
        WriteUser("jamespc");

        Assert.Equal(1, ProtonPrefixService.EnsureSkyrimLocalAppData(_pfx));
        Assert.True(Directory.Exists(GameDir("jamespc")));
    }

    [Fact]
    public void SkipsThePublicProfile()
    {
        WriteUser("steamuser");
        WriteUser("Public");

        Assert.Equal(1, ProtonPrefixService.EnsureSkyrimLocalAppData(_pfx));
        Assert.False(Directory.Exists(GameDir("Public")));
    }

    [Fact]
    public void IsANoOpWhenTheFolderAlreadyExists()
    {
        WriteUser("steamuser");
        Directory.CreateDirectory(GameDir("steamuser"));
        File.WriteAllText(Path.Join(GameDir("steamuser"), "Plugins.txt"), "keep me");

        Assert.Equal(0, ProtonPrefixService.EnsureSkyrimLocalAppData(_pfx));
        Assert.Equal("keep me", File.ReadAllText(Path.Join(GameDir("steamuser"), "Plugins.txt")));
    }

    [Fact]
    public void DoesNotThrowWhenThePrefixHasNoUsersFolder()
    {
        Assert.Equal(0, ProtonPrefixService.EnsureSkyrimLocalAppData(_pfx));
    }

    [Fact]
    public void DoesNotThrowWhenThePrefixDoesNotExist()
    {
        Assert.Equal(0, ProtonPrefixService.EnsureSkyrimLocalAppData(Path.Join(_pfx, "nope")));
    }

    private string GameDir(string user) =>
        Path.Join(_pfx, "drive_c", "users", user, "AppData", "Local", "Skyrim Special Edition");

    private void WriteUser(string user) =>
        Directory.CreateDirectory(Path.Join(_pfx, "drive_c", "users", user, "AppData", "Local"));
}
