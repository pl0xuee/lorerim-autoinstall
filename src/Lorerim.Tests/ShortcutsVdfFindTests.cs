using System;
using System.Collections.Generic;
using System.IO;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// Find locates an existing non-Steam shortcut by name so an install re-run can leave a
/// user's Steam entry untouched instead of rewriting it.
/// </summary>
public class ShortcutsVdfFindTests
{
    private static SteamInstallation TempSteam(out string root)
    {
        root = Path.Join(Path.GetTempPath(), "lorerim-find-" + Guid.NewGuid().ToString("N"));
        return new SteamInstallation(root, "12345");
    }

    [Fact]
    public void FindReturnsAnExistingShortcutMatchingByNameCaseInsensitively()
    {
        var steam = TempSteam(out var root);
        try
        {
            var svc = new ShortcutsVdfService();
            var written = svc.Upsert(
                steam,
                "LoreRim",
                "/games/mo2/ModOrganizer.exe",
                "/games/mo2",
                "%command%"
            );

            var found = svc.Find(steam, "lorerim");

            Assert.NotNull(found);
            Assert.Equal(written.SignedAppId, found!.SignedAppId);
            Assert.Equal("LoreRim", found.AppName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void FindReturnsNullWhenNoShortcutMatchesTheName()
    {
        var steam = TempSteam(out var root);
        try
        {
            var svc = new ShortcutsVdfService();
            svc.Upsert(steam, "LoreRim", "/games/mo2/ModOrganizer.exe", "/games/mo2", "%command%");

            Assert.Null(svc.Find(steam, "Wildlander"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void FindReturnsNullWhenTheOnlyMatchHasNoUsableAppId()
    {
        var steam = TempSteam(out var root);
        try
        {
            // Some third-party tools write a shortcut with appid 0, which cannot key a Proton
            // prefix or a CompatToolMapping entry. Such an entry must not be treated as reusable.
            Directory.CreateDirectory(steam.UserConfigDir);
            var vdf = new Dictionary<string, object>
            {
                ["shortcuts"] = new Dictionary<string, object>
                {
                    ["0"] = new Dictionary<string, object>
                    {
                        ["appid"] = 0,
                        ["AppName"] = "LoreRim",
                    },
                },
            };
            File.WriteAllBytes(steam.ShortcutsVdfPath, BinaryVdf.Write(vdf));

            var svc = new ShortcutsVdfService();
            Assert.Null(svc.Find(steam, "LoreRim"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
