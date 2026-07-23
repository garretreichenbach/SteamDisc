using System.Globalization;
using SteamDisc.Core.Vdf;

namespace SteamDisc.Core.Steam;

/// <summary>
/// A typed view over an <c>appmanifest_&lt;appid&gt;.acf</c> file.
/// </summary>
/// <remarks>
/// The model is a facade over the underlying <see cref="KvNode"/> rather than a set of
/// fields we deserialise into and back out of. That is deliberate: a real manifest contains
/// keys we do not model (and Valve adds more over time), and dropping any of them on a
/// transplant is precisely the kind of infidelity that makes Steam decide to re-download the
/// whole game. Everything we do not understand rides along untouched.
/// </remarks>
public sealed class AppManifest
{
    public const string RootKey = "AppState";

    private AppManifest(KvNode root) => Root = root;

    /// <summary>The raw <c>AppState</c> node. Mutating it mutates the manifest.</summary>
    public KvNode Root { get; }

    public static AppManifest Load(string path)
    {
        var node = VdfTextReader.ParseFile(path);
        return FromNode(node);
    }

    public static AppManifest Parse(string text) => FromNode(VdfTextReader.Parse(text));

    private static AppManifest FromNode(KvNode node)
    {
        // Tolerate both a bare AppState root and a document wrapped in a synthetic root.
        if (!string.Equals(node.Key, RootKey, StringComparison.OrdinalIgnoreCase))
        {
            var inner = node.Find(RootKey);
            if (inner is null)
            {
                throw new InvalidDataException(
                    $"Not an app manifest: expected a root '{RootKey}' node but found '{node.Key}'.");
            }

            node = inner;
        }

        return new AppManifest(node);
    }

    /// <summary>Creates a minimal, synthesised manifest. Prefer transplanting a real one.</summary>
    public static AppManifest CreateMinimal(uint appId, string name, string installDir)
    {
        var root = KvNode.Object(RootKey);
        root.SetUInt64("appid", appId);
        root.SetString("Universe", "1");
        root.SetString("name", name);
        root.SetInt64("StateFlags", (long)AppStateFlagPresets.Installed);
        root.SetString("installdir", installDir);
        root.SetInt64("LastUpdated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        root.SetInt64("SizeOnDisk", 0);
        root.SetInt64("StagingSize", 0);
        root.SetInt64("buildid", 0);
        root.SetString("LastOwner", "0");
        root.SetInt64("UpdateResult", 0);
        root.SetInt64("BytesToDownload", 0);
        root.SetInt64("BytesDownloaded", 0);
        root.SetInt64("BytesToStage", 0);
        root.SetInt64("BytesStaged", 0);
        root.SetInt64("TargetBuildID", 0);
        root.SetInt64("AutoUpdateBehavior", 0);
        root.SetInt64("AllowOtherDownloadsWhileRunning", 0);
        root.SetInt64("ScheduledAutoUpdate", 0);
        root.GetOrAddObject("InstalledDepots");
        return new AppManifest(root);
    }

    public uint AppId
    {
        get => Root.GetUInt32("appid");
        set => Root.SetUInt64("appid", value);
    }

    public string Name
    {
        get => Root.GetString("name") ?? $"App {AppId}";
        set => Root.SetString("name", value);
    }

    /// <summary>Folder name under <c>steamapps/common</c>. Not necessarily equal to <see cref="Name"/>.</summary>
    public string InstallDir
    {
        get => Root.GetString("installdir") ?? Name;
        set => Root.SetString("installdir", value);
    }

    public AppStateFlags StateFlags
    {
        get => (AppStateFlags)Root.GetInt64("StateFlags");
        set => Root.SetInt64("StateFlags", (long)value);
    }

    /// <summary>
    /// The build the installed files correspond to. If this disagrees with what Steam believes
    /// is current, the client queues a download — the central risk in the whole design.
    /// </summary>
    public long BuildId
    {
        get => Root.GetInt64("buildid");
        set => Root.SetInt64("buildid", value);
    }

    public long TargetBuildId
    {
        get => Root.GetInt64("TargetBuildID");
        set => Root.SetInt64("TargetBuildID", value);
    }

    public long SizeOnDisk
    {
        get => Root.GetInt64("SizeOnDisk");
        set => Root.SetInt64("SizeOnDisk", value);
    }

    /// <summary>SteamID64 of the account the install belongs to.</summary>
    public ulong LastOwner
    {
        get => Root.GetUInt64("LastOwner");
        set => Root.SetUInt64("LastOwner", value);
    }

    public DateTimeOffset LastUpdated
    {
        get => DateTimeOffset.FromUnixTimeSeconds(Root.GetInt64("LastUpdated"));
        set => Root.SetInt64("LastUpdated", value.ToUnixTimeSeconds());
    }

    public string? LauncherPath
    {
        get => Root.GetString("LauncherPath");
        set
        {
            if (value is null)
            {
                Root.Remove("LauncherPath");
            }
            else
            {
                Root.SetString("LauncherPath", value);
            }
        }
    }

    /// <summary>Depots that make up this install, keyed by depot id.</summary>
    public IReadOnlyList<InstalledDepot> InstalledDepots
    {
        get
        {
            var node = Root.Find("InstalledDepots");
            if (node is not { IsObject: true })
            {
                return Array.Empty<InstalledDepot>();
            }

            var depots = new List<InstalledDepot>();
            foreach (var child in node.Children)
            {
                if (!child.IsObject || !uint.TryParse(child.Key, out var depotId))
                {
                    continue;
                }

                depots.Add(new InstalledDepot(
                    depotId,
                    child.GetString("manifest") ?? "0",
                    child.GetInt64("size"),
                    child.GetString("dlcappid") is { } dlc && uint.TryParse(dlc, out var dlcId) ? dlcId : null));
            }

            return depots;
        }
    }

    public string ToVdf() => VdfTextWriter.Write(Root);

    public void Save(string path) => VdfTextWriter.WriteFile(path, Root);

    /// <summary>Conventional file name for a manifest, e.g. <c>appmanifest_620.acf</c>.</summary>
    public static string FileNameFor(uint appId)
        => string.Create(CultureInfo.InvariantCulture, $"appmanifest_{appId}.acf");

    public AppManifest Clone() => new(Root.Clone());
}

/// <summary>One entry of <c>AppState.InstalledDepots</c>.</summary>
/// <param name="DepotId">Depot id.</param>
/// <param name="ManifestId">Depot manifest id — the exact content revision installed.</param>
/// <param name="Size">Depot size in bytes as recorded by Steam.</param>
/// <param name="DlcAppId">Owning DLC app id, when the depot belongs to a DLC.</param>
public readonly record struct InstalledDepot(uint DepotId, string ManifestId, long Size, uint? DlcAppId);
