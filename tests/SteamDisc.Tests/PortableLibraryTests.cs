using SteamDisc.Core.Steam;
using SteamDisc.Install;

namespace SteamDisc.Tests;

/// <summary>
/// USB round trip: write a game onto a "drive" as a raw library, register the drive with a
/// second Steam (play from it), and install from it into that Steam's own library.
/// </summary>
public class PortableLibraryTests
{
    [Fact]
    public async Task Writes_a_playable_drive_that_registers_and_installs_elsewhere()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var source = authoring.InstallGame();
        var drive = temp.CreateSubdirectory("usb");

        var written = await PortableLibrary.WriteAsync(source, drive, excludeRelativePaths: new[] { "data/nested" });

        var usb = new SteamLibrary(drive);
        Assert.True(usb.IsPortable);
        Assert.Empty(written.Warnings);
        Assert.False(Directory.Exists(Path.Combine(usb.InstallPath("Portal 2"), "data", "nested")));

        var onDrive = Assert.Single(usb.GetInstalledApps());
        Assert.Equal(source.Manifest.BuildId, onDrive.Manifest.BuildId);
        Assert.Equal(FakeSteam.AuthoringSteamId, onDrive.Manifest.LastOwner);
        Assert.Equal((long)AutoUpdateBehavior.OnlyOnLaunch, onDrive.Manifest.Root.GetInt64("AutoUpdateBehavior"));

        // Re-running skips everything already there: an interrupted copy resumes.
        var again = await PortableLibrary.WriteAsync(source, drive, excludeRelativePaths: new[] { "data/nested" });
        Assert.Equal(again.Files, again.SkippedFiles);

        // Play from the drive: another machine's Steam lists it, once.
        var other = FakeSteam.Create(temp.CreateSubdirectory("other-steam"));
        Assert.True(other.Installation.RegisterLibrary(drive, usb.EnsureLibraryFolderFile()));
        Assert.False(other.Installation.RegisterLibrary(drive, usb.EnsureLibraryFolderFile()));
        Assert.Contains(other.Installation.GetLibraries(), l => l.Path == usb.Path);
        Assert.Contains(other.Installation.GetInstalledApps(), a => a.AppId == source.AppId);

        // Install from the drive into that machine's own library.
        var local = new SteamLibrary(other.Root);
        var outcome = await PortableLibrary.InstallAsync(
            other.Installation, usb, local, new UnattendedInstallHost(), processRunner: new FakeProcessRunner());

        Assert.True(outcome.Succeeded, outcome.Message);
        var installed = local.FindApp(source.AppId);
        Assert.NotNull(installed);
        Assert.Equal(FakeSteam.LocalSteamId, installed!.Manifest.LastOwner);
        Assert.True(installed.IsFullyInstalled);
    }
}
