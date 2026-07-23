namespace SteamDisc.Tests;

/// <summary>A scratch directory that cleans itself up.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "steamdisc-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
        => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>Creates a subdirectory and returns its full path.</summary>
    public string CreateSubdirectory(string name)
    {
        var path = Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file, creating parent directories as needed.</summary>
    public string WriteFile(string relativePath, string content)
    {
        var path = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteFile(string relativePath, byte[] content)
    {
        var path = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked handle on a test file is not worth failing a test run over.
        }
    }
}

/// <summary>Deterministic pseudo-random content, so a failure reproduces exactly.</summary>
public static class TestData
{
    /// <summary>Bytes that do not compress, standing in for game assets that are already packed.</summary>
    public static byte[] Incompressible(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Highly compressible bytes, for exercising the compressed path.</summary>
    public static byte[] Compressible(int length, int seed)
    {
        var bytes = new byte[length];
        var pattern = $"steamdisc-test-{seed}-";
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)pattern[i % pattern.Length];
        }

        return bytes;
    }

    /// <summary>Builds a small tree that resembles a game folder.</summary>
    public static long CreateGameFolder(string root, int seed = 1)
    {
        Directory.CreateDirectory(root);

        long total = 0;

        void Write(string relativePath, byte[] content)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            total += content.Length;
        }

        Write("game.exe", Incompressible(64 * 1024, seed));
        Write("readme.txt", Compressible(32 * 1024, seed + 1));
        Write("data/pak01.vpk", Incompressible(512 * 1024, seed + 2));
        Write("data/strings.txt", Compressible(128 * 1024, seed + 3));
        Write("data/nested/deep/asset.bin", Incompressible(96 * 1024, seed + 4));
        Write("_CommonRedist/vcredist/2019/VC_redist.x64.exe", Compressible(4096, seed + 5));

        // An empty directory, to prove directory records survive the round trip.
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        return total;
    }
}
