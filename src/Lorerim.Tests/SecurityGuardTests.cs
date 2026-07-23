using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Nexus;
using Xunit;

namespace Lorerim.Tests;

public class SecurityGuardTests
{
    [Theory]
    [InlineData("steam://install/489830")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://open?x=1")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void NonWebUrlsAreRefused(string? url)
    {
        Assert.False(SafeUrl.TryOpenInBrowser(url));
    }

    [Fact]
    public void DesktopExecQuotingEscapesSpecFields()
    {
        // % doubles; backslash and quote are backslash-escaped inside the quoted arg.
        Assert.Equal(
            "\"/opt/100%% mods/app\"",
            ProtocolHandlerRegistrar.QuoteExecArg("/opt/100% mods/app")
        );
        Assert.Equal(
            "\"/x/\\\"quoted\\\"/a\\\\b\"",
            ProtocolHandlerRegistrar.QuoteExecArg("/x/\"quoted\"/a\\b")
        );
    }

    [Theory]
    [InlineData("/home/user/Games/LoreRim", null)]
    [InlineData("/mnt/ssd my games/LoreRim", null)]
    [InlineData("/home/user/we\"ird", "a double quote")]
    [InlineData("/home/user/a:b", "a colon")]
    [InlineData("/home/user/line\nbreak", "a control character")]
    public void PathProblemFlagsUnsafeFolderNames(string dir, string? expectedFragment)
    {
        var problem = PreflightService.PathProblem(dir);
        if (expectedFragment is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains(expectedFragment, problem);
        }
    }
}
