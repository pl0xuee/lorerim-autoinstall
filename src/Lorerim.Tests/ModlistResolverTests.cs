using Lorerim.Gui.Services.Modlist;
using Xunit;

namespace Lorerim.Tests;

public class ModlistResolverTests
{
    private const string CatalogOutput = """
        Loading all modlist definitions
        Loaded 217 lists
        Showing 1 modlists after filtering
        LoreRim - Skyrim Special Edition - 233.3 GB|304.2 GB|537.5 GB - LoreRim/LoreRim
        """;

    [Fact]
    public void ParsesLorerimEntry()
    {
        var info = ModlistResolverService.ParseEntry(CatalogOutput);
        Assert.NotNull(info);
        Assert.Equal("LoreRim/LoreRim", info.MachineUrl);
        Assert.Equal("LoreRim", info.Title);
        Assert.Equal((long)(233.3 * 1024 * 1024 * 1024), info.DownloadSizeBytes);
        Assert.Equal((long)(304.2 * 1024 * 1024 * 1024), info.InstallSizeBytes);
    }

    [Fact]
    public void ReturnsNullWhenAbsent()
    {
        Assert.Null(ModlistResolverService.ParseEntry("Loaded 217 lists\nNothing here"));
    }
}
