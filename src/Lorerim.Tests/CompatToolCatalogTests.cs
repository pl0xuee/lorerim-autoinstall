using System;
using System.IO;
using System.Linq;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// A Proton build runs inside the Steam Linux Runtime its toolmanifest pins. Reading that
/// pin is what lets the app notice a build cannot run before an install depends on it.
/// </summary>
public class CompatToolCatalogTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("lorerim-compat-test")
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
    public void ReadsTheRequiredRuntimeAppId()
    {
        WriteTool("GE-Proton10-34", toolManifest: Manifest("1628350"));

        Assert.Equal(1628350, Scan("GE-Proton10-34").RequiredRuntimeAppId);
    }

    [Fact]
    public void ToolWithoutAManifestDeclaresNoRequirement()
    {
        WriteTool("GE-Proton10-34", toolManifest: null);

        Assert.Null(Scan("GE-Proton10-34").RequiredRuntimeAppId);
    }

    [Fact]
    public void MalformedManifestDeclaresNoRequirement()
    {
        WriteTool("GE-Proton10-34", toolManifest: "\"manifest\" { not a vdf at all");

        Assert.Null(Scan("GE-Proton10-34").RequiredRuntimeAppId);
    }

    [Fact]
    public void NonNumericAppIdDeclaresNoRequirement()
    {
        WriteTool("GE-Proton10-34", toolManifest: Manifest("sniper"));

        Assert.Null(Scan("GE-Proton10-34").RequiredRuntimeAppId);
    }

    // No well-known dirs: the host's own Proton builds must not leak into these fixtures.
    private CompatTool Scan(string internalName) =>
        new CompatToolCatalog(wellKnownDirs: [])
            .Scan(_root)
            .Single(t => t.InternalName == internalName);

    private static string Manifest(string appId) =>
        $$"""
        "manifest"
        {
          "version" "2"
          "commandline" "/proton %verb%"
          "require_tool_appid" "{{appId}}"
        }
        """;

    private void WriteTool(string internalName, string? toolManifest)
    {
        var dir = Path.Join(_root, "compatibilitytools.d", internalName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Join(dir, "compatibilitytool.vdf"),
            $$"""
            "compatibilitytools"
            {
              "compat_tools"
              {
                "{{internalName}}"
                {
                  "install_path" "."
                  "display_name" "{{internalName}}"
                }
              }
            }
            """
        );
        File.WriteAllText(Path.Join(dir, "proton"), "#!/usr/bin/env python3\n");
        if (toolManifest is not null)
        {
            File.WriteAllText(Path.Join(dir, "toolmanifest.vdf"), toolManifest);
        }
    }
}
