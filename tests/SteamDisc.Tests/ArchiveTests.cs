using SteamDisc.Core.Archive;
using SteamDisc.Core.Progress;

namespace SteamDisc.Tests;

public class ArchiveTests
{
    [Theory]
    [InlineData(ArchiveCompression.Store)]
    [InlineData(ArchiveCompression.Fast)]
    [InlineData(ArchiveCompression.Maximum)]
    public async Task Round_trips_a_game_folder(ArchiveCompression compression)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        var archive = temp.CreateSubdirectory("archive");
        var destination = temp.CreateSubdirectory("destination");

        var expectedBytes = TestData.CreateGameFolder(source);

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source, archive, "payload", SdzFormat.DefaultVolumeSize, compression));

        Assert.Equal(expectedBytes, created.UncompressedBytes);
        Assert.Single(created.VolumePaths);

        var extracted = await engine.ExtractAsync(new ArchiveExtractRequest(
            new FileVolumeSource(created.VolumePaths), destination));

        Assert.Equal(created.FileCount, extracted.FileCount);
        Assert.Equal(expectedBytes, extracted.UncompressedBytes);
        AssertDirectoriesMatch(source, destination);
    }

    [Fact]
    public async Task Splits_across_volumes_and_reassembles()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        var archive = temp.CreateSubdirectory("archive");
        var destination = temp.CreateSubdirectory("destination");

        TestData.CreateGameFolder(source);

        // A volume size well below the payload forces several volumes, and forces at least one
        // file to straddle a boundary — the case that matters for multi-disc sets.
        const long volumeSize = 100 * 1024;

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source, archive, "payload", volumeSize, ArchiveCompression.Store));

        Assert.True(created.VolumePaths.Count > 5, $"expected several volumes, got {created.VolumePaths.Count}");

        foreach (var volume in created.VolumePaths.SkipLast(1))
        {
            Assert.Equal(volumeSize, new FileInfo(volume).Length);
        }

        var extracted = await engine.ExtractAsync(new ArchiveExtractRequest(
            new FileVolumeSource(created.VolumePaths), destination));

        Assert.Equal(created.FileCount, extracted.FileCount);
        AssertDirectoriesMatch(source, destination);
    }

    [Fact]
    public async Task Volume_names_follow_the_documented_convention()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        TestData.CreateGameFolder(source);

        var created = await new SdzArchiveEngine().CreateAsync(new ArchiveCreateRequest(
            source, temp.CreateSubdirectory("archive"), "payload", 100 * 1024, ArchiveCompression.Store));

        Assert.EndsWith("payload.sdz.001", created.VolumePaths[0], StringComparison.Ordinal);
        Assert.EndsWith("payload.sdz.002", created.VolumePaths[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detects_a_corrupted_volume()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        var archive = temp.CreateSubdirectory("archive");
        var destination = temp.CreateSubdirectory("destination");

        TestData.CreateGameFolder(source);

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source, archive, "payload", SdzFormat.DefaultVolumeSize, ArchiveCompression.Store));

        // Flip bytes deep inside the volume, simulating a scratched disc rather than a
        // truncated file.
        var bytes = await File.ReadAllBytesAsync(created.VolumePaths[0]);
        for (var i = bytes.Length / 2; i < (bytes.Length / 2) + 512; i++)
        {
            bytes[i] ^= 0xFF;
        }

        await File.WriteAllBytesAsync(created.VolumePaths[0], bytes);

        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExtractAsync(
            new ArchiveExtractRequest(new FileVolumeSource(created.VolumePaths), destination)));
    }

    [Fact]
    public async Task Detects_a_truncated_volume()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        var archive = temp.CreateSubdirectory("archive");

        TestData.CreateGameFolder(source);

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source, archive, "payload", SdzFormat.DefaultVolumeSize, ArchiveCompression.Store));

        var bytes = await File.ReadAllBytesAsync(created.VolumePaths[0]);
        await File.WriteAllBytesAsync(created.VolumePaths[0], bytes[..(bytes.Length / 2)]);

        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExtractAsync(
            new ArchiveExtractRequest(
                new FileVolumeSource(created.VolumePaths),
                temp.CreateSubdirectory("destination"))));
    }

    [Fact]
    public async Task Rejects_an_archive_that_is_not_ours()
    {
        using var temp = new TempDirectory();
        var bogus = temp.WriteFile("not-an-archive.sdz.001", "this is not a SteamDisc archive");

        var exception = await Assert.ThrowsAsync<ArchiveIntegrityException>(() =>
            new SdzArchiveEngine().ExtractAsync(new ArchiveExtractRequest(
                new FileVolumeSource(new[] { bogus }),
                temp.CreateSubdirectory("destination"))));

        Assert.Contains("not a SteamDisc archive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_progress_that_reaches_completion()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        TestData.CreateGameFolder(source);

        var reports = new List<OperationProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await new SdzArchiveEngine().CreateAsync(
            new ArchiveCreateRequest(source, temp.CreateSubdirectory("archive"), "payload", SdzFormat.DefaultVolumeSize),
            progress);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Fraction);

        // Progress must never go backwards; a bar that jumps around reads as broken.
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].BytesCompleted >= reports[i - 1].BytesCompleted);
        }
    }

    [Fact]
    public async Task Excludes_requested_paths()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        var destination = temp.CreateSubdirectory("destination");
        TestData.CreateGameFolder(source);

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source,
            temp.CreateSubdirectory("archive"),
            "payload",
            SdzFormat.DefaultVolumeSize,
            ArchiveCompression.Store,
            new[] { "_CommonRedist" }));

        await engine.ExtractAsync(new ArchiveExtractRequest(new FileVolumeSource(created.VolumePaths), destination));

        Assert.False(Directory.Exists(Path.Combine(destination, "_CommonRedist")));
        Assert.True(File.Exists(Path.Combine(destination, "game.exe")));
    }

    [Fact]
    public async Task Lists_contents_without_extracting()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("source");
        TestData.CreateGameFolder(source);

        var engine = new SdzArchiveEngine();
        var created = await engine.CreateAsync(new ArchiveCreateRequest(
            source, temp.CreateSubdirectory("archive"), "payload", SdzFormat.DefaultVolumeSize));

        var entries = await engine.ListAsync(new FileVolumeSource(created.VolumePaths));

        Assert.Contains(entries, e => e.Path == "game.exe" && !e.IsDirectory);
        Assert.Contains(entries, e => e.Path == "empty" && e.IsDirectory);
        Assert.All(entries.Where(e => !e.IsDirectory), e => Assert.NotNull(e.Sha256));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/absolute.txt")]
    public void Rejects_paths_that_escape_the_destination(string malicious)
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArchiveIntegrityException>(
            () => SdzArchiveEngine.ResolveSafePath(temp.Path, malicious));
    }

    [Fact]
    public void Accepts_ordinary_nested_paths()
    {
        using var temp = new TempDirectory();

        var resolved = SdzArchiveEngine.ResolveSafePath(temp.Path, "data/nested/asset.bin");

        Assert.StartsWith(temp.Path, resolved, StringComparison.Ordinal);
        Assert.EndsWith("asset.bin", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_reports_unknown_formats_clearly()
    {
        var registry = ArchiveEngineRegistry.CreateDefault();

        Assert.NotNull(registry.Find(ArchiveFormats.Sdz));
        Assert.Throws<NotSupportedException>(() => registry.Require("rar"));
    }

    internal static void AssertDirectoriesMatch(string expectedRoot, string actualRoot)
    {
        var expected = EnumerateRelative(expectedRoot);
        var actual = EnumerateRelative(actualRoot);

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (relative, expectedPath) in expected)
        {
            Assert.Equal(
                File.ReadAllBytes(expectedPath),
                File.ReadAllBytes(actual[relative]));
        }
    }

    private static Dictionary<string, string> EnumerateRelative(string root)
        => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'),
                f => f,
                StringComparer.Ordinal);
}

/// <summary>
/// <see cref="Progress{T}"/> posts to a synchronisation context, which reorders reports in a
/// test. This calls straight through.
/// </summary>
internal sealed class SynchronousProgress : IProgress<OperationProgress>
{
    private readonly Action<OperationProgress> _handler;

    public SynchronousProgress(Action<OperationProgress> handler) => _handler = handler;

    public void Report(OperationProgress value) => _handler(value);
}
