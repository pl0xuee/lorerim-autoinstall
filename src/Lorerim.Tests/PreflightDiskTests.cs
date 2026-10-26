using System;
using System.IO;
using Lorerim.Gui.Services;
using Xunit;

namespace Lorerim.Tests;

/// <summary>
/// The disk-type check has to find the physical disk under a mount. A mount source is rarely
/// the disk itself: an encrypted or LVM root is a device-mapper node (dm-0) and a plain one is
/// a partition (nvme0n1p2). The check used to match only the second shape, so every LUKS
/// install was reported as "unknown device" and never examined.
/// </summary>
public class PreflightDiskTests : IDisposable
{
    private readonly string _sys = Directory.CreateTempSubdirectory("lorerim-sysblock").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sys, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup trouble must not turn into a spurious test failure.
        }
    }

    /// <summary>Lays out sysfs the way the kernel does: a disk, its partitions nested inside, and
    /// /sys/class/block symlinking every node.</summary>
    private string AddDisk(string disk, params string[] partitions)
    {
        var devices = Path.Join(_sys, "devices");
        var diskDir = Directory.CreateDirectory(Path.Join(devices, disk)).FullName;
        Directory.CreateSymbolicLink(Path.Join(ClassBlock, disk), diskDir);
        foreach (var part in partitions)
        {
            var partDir = Directory.CreateDirectory(Path.Join(diskDir, part)).FullName;
            File.WriteAllText(Path.Join(partDir, "partition"), "1\n");
            Directory.CreateSymbolicLink(Path.Join(ClassBlock, part), partDir);
        }
        return diskDir;
    }

    private string ClassBlock
    {
        get
        {
            var path = Path.Join(_sys, "class", "block");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    [Fact]
    public void ADiskResolvesToItself()
    {
        AddDisk("sda");
        Assert.Equal("sda", PreflightService.ResolveDisk(ClassBlock, "sda"));
    }

    [Fact]
    public void APartitionResolvesToItsParentDisk()
    {
        AddDisk("nvme0n1", "nvme0n1p1", "nvme0n1p2");
        Assert.Equal("nvme0n1", PreflightService.ResolveDisk(ClassBlock, "nvme0n1p2"));
    }

    [Fact]
    public void ADeviceMapperNodeResolvesThroughItsSlavesToThePhysicalDisk()
    {
        AddDisk("nvme0n1", "nvme0n1p2");
        var dm = Directory.CreateDirectory(Path.Join(_sys, "devices", "dm-0")).FullName;
        Directory.CreateSymbolicLink(Path.Join(ClassBlock, "dm-0"), dm);
        Directory.CreateDirectory(Path.Join(dm, "slaves"));
        Directory.CreateSymbolicLink(
            Path.Join(dm, "slaves", "nvme0n1p2"),
            Path.Join(ClassBlock, "nvme0n1p2")
        );

        Assert.Equal("nvme0n1", PreflightService.ResolveDisk(ClassBlock, "dm-0"));
    }

    [Fact]
    public void StackedMappersAreFollowedToTheBottom()
    {
        AddDisk("sda", "sda3");
        string Mapper(string name, string below)
        {
            var dir = Directory.CreateDirectory(Path.Join(_sys, "devices", name)).FullName;
            Directory.CreateSymbolicLink(Path.Join(ClassBlock, name), dir);
            Directory.CreateDirectory(Path.Join(dir, "slaves"));
            Directory.CreateSymbolicLink(
                Path.Join(dir, "slaves", below),
                Path.Join(ClassBlock, below)
            );
            return dir;
        }
        Mapper("dm-0", "sda3");
        Mapper("dm-1", "dm-0");

        Assert.Equal("sda", PreflightService.ResolveDisk(ClassBlock, "dm-1"));
    }

    [Fact]
    public void AnUnknownDeviceResolvesToNothingRatherThanGuessing()
    {
        Assert.Null(PreflightService.ResolveDisk(ClassBlock, "nope0"));
    }

    [Fact]
    public void AMapperLoopCannotRecurseForever()
    {
        var a = Directory.CreateDirectory(Path.Join(_sys, "devices", "dm-a")).FullName;
        var b = Directory.CreateDirectory(Path.Join(_sys, "devices", "dm-b")).FullName;
        Directory.CreateSymbolicLink(Path.Join(ClassBlock, "dm-a"), a);
        Directory.CreateSymbolicLink(Path.Join(ClassBlock, "dm-b"), b);
        Directory.CreateDirectory(Path.Join(a, "slaves"));
        Directory.CreateDirectory(Path.Join(b, "slaves"));
        Directory.CreateSymbolicLink(Path.Join(a, "slaves", "dm-b"), Path.Join(ClassBlock, "dm-b"));
        Directory.CreateSymbolicLink(Path.Join(b, "slaves", "dm-a"), Path.Join(ClassBlock, "dm-a"));

        Assert.Null(PreflightService.ResolveDisk(ClassBlock, "dm-a"));
    }
}

public class EngineVersionDisplayTests
{
    [Theory]
    [InlineData("0.5.10+e1981db487b06b5b798370eec6273cefffd0ef26\n", "jackify-engine 0.5.10")]
    [InlineData("0.5.10", "jackify-engine 0.5.10")]
    [InlineData("  0.6.0+abc  \r\n", "jackify-engine 0.6.0")]
    public void ShowsTheVersionWithoutTheBuildHash(string raw, string expected) =>
        Assert.Equal(expected, PreflightService.FormatEngineVersion(raw));

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public void SaysSoWhenTheEngineReportsNothing(string raw) =>
        Assert.Contains("not reported", PreflightService.FormatEngineVersion(raw));
}
