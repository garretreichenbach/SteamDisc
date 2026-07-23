using SteamDisc.Core.Vdf;

namespace SteamDisc.Tests;

public class VdfTests
{
    private const string PortalManifest = """
        "AppState"
        {
        	"appid"		"620"
        	"Universe"		"1"
        	"name"		"Portal 2"
        	"StateFlags"		"4"
        	"installdir"		"Portal 2"
        	"LastUpdated"		"1690000000"
        	"SizeOnDisk"		"14041947036"
        	"buildid"		"7415828"
        	"LastOwner"		"76561198000000000"
        	"InstalledDepots"
        	{
        		"621"
        		{
        			"manifest"		"5106065882179239892"
        			"size"		"10334085297"
        		}
        		"622"
        		{
        			"manifest"		"1234567890123456789"
        			"size"		"3707861739"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void Parses_nested_objects_and_values()
    {
        var root = VdfTextReader.Parse(PortalManifest);

        Assert.Equal("AppState", root.Key);
        Assert.Equal("620", root.GetString("appid"));
        Assert.Equal("Portal 2", root.GetString("name"));

        var depots = root["InstalledDepots"];
        Assert.NotNull(depots);
        Assert.Equal(2, depots!.Children.Count);
        Assert.Equal("5106065882179239892", depots["621"]!.GetString("manifest"));
    }

    [Fact]
    public void Key_lookup_is_case_insensitive()
    {
        var root = VdfTextReader.Parse(PortalManifest);

        Assert.Equal("620", root.GetString("AppID"));
        Assert.NotNull(root["installedDEPOTS"]);
    }

    [Fact]
    public void Round_trips_without_losing_content()
    {
        var original = VdfTextReader.Parse(PortalManifest);
        var written = VdfTextWriter.Write(original);
        var reparsed = VdfTextReader.Parse(written);

        // Writing and re-reading must be a fixed point: anything lost on the first pass would
        // silently degrade a transplanted manifest.
        Assert.Equal(written, VdfTextWriter.Write(reparsed));
        Assert.Equal("Portal 2", reparsed.GetString("name"));
        Assert.Equal(2, reparsed["InstalledDepots"]!.Children.Count);
    }

    [Fact]
    public void Preserves_unknown_keys_through_a_round_trip()
    {
        var text = """
            "AppState"
            {
            	"appid"		"1"
            	"SomeFutureValveKey"		"42"
            	"NestedFuture"
            	{
            		"a"		"b"
            	}
            }
            """;

        var written = VdfTextWriter.Write(VdfTextReader.Parse(text));

        Assert.Contains("SomeFutureValveKey", written, StringComparison.Ordinal);
        Assert.Contains("NestedFuture", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_round_trip_for_windows_paths()
    {
        var text = "\"root\"\n{\n\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n}";
        var root = VdfTextReader.Parse(text);

        Assert.Equal(@"C:\Program Files (x86)\Steam", root.GetString("path"));

        var written = VdfTextWriter.Write(root);
        Assert.Contains(@"C:\\Program Files (x86)\\Steam", written, StringComparison.Ordinal);
        Assert.Equal(@"C:\Program Files (x86)\Steam", VdfTextReader.Parse(written).GetString("path"));
    }

    [Fact]
    public void Handles_comments_and_bare_tokens()
    {
        var text = """
            // a leading comment
            "root"
            {
            	"quoted"		"value"   // trailing comment
            	bare		token
            }
            """;

        var root = VdfTextReader.Parse(text);

        Assert.Equal("value", root.GetString("quoted"));
        Assert.Equal("token", root.GetString("bare"));
    }

    [Fact]
    public void Preserves_platform_conditionals()
    {
        var text = "\"root\"\n{\n\t\"key\"\t\t\"value\" [$WIN32]\n}";
        var root = VdfTextReader.Parse(text);

        Assert.Equal("$WIN32", root["key"]!.Condition);
        Assert.Contains("[$WIN32]", VdfTextWriter.Write(root), StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_duplicate_keys()
    {
        var text = "\"root\"\n{\n\t\"dup\"\t\t\"1\"\n\t\"dup\"\t\t\"2\"\n}";
        var root = VdfTextReader.Parse(text);

        Assert.Equal(2, root.FindAll("dup").Count());
        Assert.Equal("1", root.GetString("dup"));
    }

    [Fact]
    public void SetString_updates_in_place_rather_than_appending()
    {
        var root = VdfTextReader.Parse(PortalManifest);
        var originalCount = root.Children.Count;

        root.SetString("name", "Portal 2 (edited)");

        Assert.Equal(originalCount, root.Children.Count);
        Assert.Equal("Portal 2 (edited)", root.GetString("name"));

        // The edited key must stay where it was, not jump to the end.
        Assert.Equal(2, root.Children.ToList().FindIndex(c => c.Key == "name"));
    }

    [Fact]
    public void Rejects_unterminated_objects()
    {
        var exception = Assert.Throws<VdfSyntaxException>(() => VdfTextReader.Parse("\"root\"\n{\n\t\"a\" \"b\"\n"));
        Assert.True(exception.Line > 0);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var original = VdfTextReader.Parse(PortalManifest);
        var copy = original.Clone();

        copy["InstalledDepots"]!["621"]!.SetString("manifest", "999");

        Assert.Equal("5106065882179239892", original["InstalledDepots"]!["621"]!.GetString("manifest"));
        Assert.Equal("999", copy["InstalledDepots"]!["621"]!.GetString("manifest"));
    }
}
