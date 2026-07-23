using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lorerim.Gui.Services.Modlist;

/// <summary>
/// JContainers SE ships a DLL that crashes under Wine/Proton in every Nexus release up to
/// 4.2.9.0, which takes LoreRim down with it (Wheeler and others depend on it). The author's
/// patched build fixes it but has not been published to Nexus, so the engine always installs
/// the broken one and it has to be swapped out afterwards. Same fix Jackify applies in its
/// configure phase (modlist_fixup_handler.py).
/// </summary>
public static class JContainersFix
{
    public const string DllName = "JContainers64.dll";

    /// <summary>
    /// The patched DLL is compiled against one SKSE runtime; installing it over any other
    /// would break a working setup, so detection refuses to act unless this one is present.
    /// </summary>
    public const string TargetSkseDll = "skse64_1_6_1170.dll";

    public const string FixedSha256 =
        "4c00d7194c61097361e8f93521d79752a975fca5f54446e391debb0c56999590";

    /// <summary>
    /// SHA-256 of the release archive itself. GitHub assets are mutable — an asset can be
    /// replaced under the same URL — so the archive is verified before it is ever handed to
    /// the extractor, not just the DLL that comes out of it.
    /// </summary>
    public const string ArchiveSha256 =
        "5e407bc6246607c3936e3d10f1c966c671c67267980b2717f82ade2c6b5c8ad1";

    public const string DownloadUrl =
        "https://github.com/rfortier/JContainers-rwf/releases/download/v4.2.13.2/"
        + "JContainers64-v4.2.13.2.for.1.6.1170.patch.luajit.with.gc64.7z";

    /// <summary>Suffix for the copy kept beside a replaced DLL so the swap stays reversible.</summary>
    public const string BackupSuffix = ".nexus.bak";

    /// <summary>Folders a modlist may use for the game root instead of a mod directory.</summary>
    private static readonly string[] GameRootFolders =
        ["Stock Game", "StockGame", "Game Root", "Stock Folder"];

    /// <summary>
    /// True when the install uses the SKSE runtime the patched build targets. Asking whether
    /// the runtime is *present* rather than "which loader turns up first" matters: one stray
    /// loader among thousands of mod folders would otherwise switch the fix off silently.
    /// </summary>
    public static bool TargetsSupportedSkse(string installDir)
    {
        var modsDir = ChildDirectory(installDir, "mods");
        var modRoots = modsDir is null
            ? []
            : ModDirectories(modsDir).Select(mod => ChildDirectory(mod, "Root"));
        var gameRoots = GameRootFolders.Select(name => ChildDirectory(installDir, name));
        return modRoots
            .Concat(gameRoots)
            .Any(dir => dir is not null && ChildFiles(dir, TargetSkseDll).Any());
    }

    /// <summary>Every JContainers DLL the install ships, whatever the build.</summary>
    public static IReadOnlyList<string> FindAll(string installDir)
    {
        var modsDir = ChildDirectory(installDir, "mods");
        if (modsDir is null)
        {
            return [];
        }
        return
        [
            .. ModDirectories(modsDir)
                .Select(mod => ChildDirectory(mod, "SKSE"))
                .Select(skse => skse is null ? null : ChildDirectory(skse, "Plugins"))
                .Where(plugins => plugins is not null)
                .SelectMany(plugins => ChildFiles(plugins!, DllName)),
        ];
    }

    public static IReadOnlyList<string> FindOutdated(string installDir) =>
        FindOutdated(installDir, FixedSha256);

    /// <summary>
    /// Every JContainers DLL that is not already the known-good build. Empty when the modlist
    /// has no JContainers or targets a different SKSE runtime — both mean "leave it alone",
    /// not "something went wrong".
    /// </summary>
    public static IReadOnlyList<string> FindOutdated(string installDir, string knownGoodSha256)
    {
        if (!TargetsSupportedSkse(installDir))
        {
            return [];
        }
        return
        [
            .. FindAll(installDir)
                .Where(dll => !Sha256Hex(dll).Equals(knownGoodSha256, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>Internal path of the DLL within a `7zz l -ba -slt` listing, or null if absent.</summary>
    public static string? DllPathInArchive(string listing) =>
        listing
            .Split("\n\n", StringSplitOptions.TrimEntries)
            .Select(ParseEntry)
            .FirstOrDefault(entry =>
                entry.Path is not null
                && !entry.IsDirectory
                // Match the entry name, not a suffix: "NotJContainers64.dll" is a different file.
                && entry.Path.Split('/', '\\')[^1].Equals(DllName, StringComparison.OrdinalIgnoreCase)
                && IsSafeEntryName(entry.Path)
            )
            .Path;

    /// <summary>
    /// The entry name is passed to 7-Zip as an argument, so a name that reads as a switch
    /// ("-w…"), a listfile ("@…"), a glob, or an escape ("../…") is refused outright rather
    /// than trusted to 7-Zip's own parsing.
    /// </summary>
    public static bool IsSafeEntryName(string entry) =>
        !entry.StartsWith('-')
        && !entry.StartsWith('@')
        && !Path.IsPathRooted(entry)
        && !entry.StartsWith('\\')
        && entry.IndexOfAny(['*', '?']) < 0
        && entry.Split('/', '\\').All(segment => segment != "..");

    /// <summary>
    /// Swaps the patched DLL in, keeping the shipped one beside it as a backup. Refuses a
    /// source whose hash does not match, and leaves the target untouched when it does.
    /// </summary>
    public static void Replace(string targetDll, string patchedDll, string expectedSha256)
    {
        // Read once and verify those exact bytes: hashing the file and then re-reading it
        // would leave a window where what was checked is not what gets written.
        var bytes = File.ReadAllBytes(patchedDll);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Patched {DllName} failed checksum verification (expected {expectedSha256}, got {actual})"
            );
        }
        var backup = targetDll + BackupSuffix;
        // Only the first backup is the shipped DLL; a later one would just save our own copy.
        // Written atomically so an interrupted run cannot leave a truncated backup that later
        // runs would trust — that would lose the original for good.
        if (!File.Exists(backup))
        {
            AtomicFile.WriteAllBytes(backup, File.ReadAllBytes(targetDll));
        }
        AtomicFile.WriteAllBytes(targetDll, bytes);
    }

    /// <summary>
    /// The 7-Zip binary the jackify-engine already bundles, so extracting the patched build
    /// costs no extra dependency and no assumption about what the host has installed.
    /// </summary>
    public static string? ExtractorPath(string? engineDir)
    {
        if (string.IsNullOrEmpty(engineDir))
        {
            return null;
        }
        var path = Path.Join(engineDir, "Extractors", "linux-x64", "7zz");
        return File.Exists(path) ? path : null;
    }

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private const string PathPrefix = "Path = ";
    private const string AttributesPrefix = "Attributes = ";

    private static (string? Path, bool IsDirectory) ParseEntry(string block)
    {
        string? path = null;
        var isDirectory = false;
        foreach (var line in block.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith(PathPrefix, StringComparison.Ordinal))
            {
                path = line[PathPrefix.Length..].Trim();
            }
            else if (line.StartsWith(AttributesPrefix, StringComparison.Ordinal))
            {
                isDirectory = line[AttributesPrefix.Length..].TrimStart().StartsWith('D');
            }
        }
        return (path, isDirectory);
    }

    /// <summary>
    /// Case-insensitive child lookup. Mod archives are authored on Windows, so extracted
    /// folders turn up as "SKSE", "skse" or "Skse"; a literal match misses real installs.
    /// </summary>
    private static string? ChildDirectory(string parent, string name) =>
        Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent, name, CaseInsensitive).FirstOrDefault()
            : null;

    private static IEnumerable<string> ChildFiles(string parent, string pattern) =>
        Directory.Exists(parent)
            ? Directory.EnumerateFiles(parent, pattern, CaseInsensitive)
            : [];

    private static readonly EnumerationOptions CaseInsensitive = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
    };

    /// <summary>Ordered so a run over the same install always reports the same list.</summary>
    private static IEnumerable<string> ModDirectories(string modsDir) =>
        Directory.EnumerateDirectories(modsDir).Order(StringComparer.Ordinal);
}
