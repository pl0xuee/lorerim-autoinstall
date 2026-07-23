using System;
using System.Threading;
using System.Threading.Tasks;
using Lorerim.Gui.Services.Steam;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The modlist compatibility pass runs at the end of Steam setup. When an earlier Steam step
/// fails the install is already complete on disk, so skipping the pass leaves a modlist that
/// crashes on launch — exactly what the pass exists to prevent.
/// </summary>
public class SteamSetupFixupOrderTests
{
    [Fact]
    public async Task FixupsRunAfterSuccessfulSteamSteps()
    {
        var applied = 0;

        await SteamIntegrationService.RunWithFixupsAsync(
            () => Task.CompletedTask,
            () =>
            {
                applied++;
                return Task.CompletedTask;
            },
            _ => { },
            CancellationToken.None
        );

        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task FixupsStillRunWhenASteamStepFails()
    {
        var applied = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                SteamIntegrationService.RunWithFixupsAsync(
                    () => throw new InvalidOperationException("protontricks failed"),
                    () =>
                    {
                        applied++;
                        return Task.CompletedTask;
                    },
                    _ => { },
                    CancellationToken.None
                )
        );

        Assert.Equal(1, applied);
        Assert.Equal("protontricks failed", failure.Message);
    }

    [Fact]
    public async Task AFailingFixupDoesNotMaskTheOriginalFailure()
    {
        var logged = "";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                SteamIntegrationService.RunWithFixupsAsync(
                    () => throw new InvalidOperationException("protontricks failed"),
                    () => throw new TimeoutException("download timed out"),
                    message => logged = message,
                    CancellationToken.None
                )
        );

        Assert.Equal("protontricks failed", failure.Message);
        Assert.Contains("download timed out", logged);
    }

    [Fact]
    public async Task FixupsAreSkippedWhenTheUserCancels()
    {
        var applied = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () =>
                SteamIntegrationService.RunWithFixupsAsync(
                    () => throw new OperationCanceledException(),
                    () =>
                    {
                        applied++;
                        return Task.CompletedTask;
                    },
                    _ => { },
                    cts.Token
                )
        );

        Assert.Equal(0, applied);
    }
}
