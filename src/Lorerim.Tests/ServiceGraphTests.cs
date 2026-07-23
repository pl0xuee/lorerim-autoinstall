using Lorerim.Gui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lorerim.Tests;

public class ServiceGraphTests
{
    /// <summary>
    /// A constructor parameter with no registration behind it compiles fine and throws on
    /// startup. ValidateOnBuild resolves every call site without invoking a single factory,
    /// so this catches that without writing to the user's log.
    /// </summary>
    [Fact]
    public void EveryRegisteredServiceCanBeResolved()
    {
        var provider = App.Registrations()
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );

        Assert.NotNull(provider);
    }
}
