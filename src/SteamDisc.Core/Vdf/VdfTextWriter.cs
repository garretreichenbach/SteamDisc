using System.Text;

namespace SteamDisc.Core.Vdf;

/// <summary>
/// Serialises a <see cref="KvNode"/> tree back to Valve's text KeyValues format, matching
/// the layout Steam itself writes: tab indentation, and two tabs between a key and its value.
/// </summary>
public static class VdfTextWriter
{
    /// <summary>Steam's own files use CRLF even on non-Windows platforms; keep that.</summary>
    public const string NewLine = "\r\n";

    public static string Write(KvNode root)
    {
        var builder = new StringBuilder();
        WriteNode(builder, root, depth: 0);
        return builder.ToString();
    }

    /// <summary>
    /// Writes to <paramref name="path"/> atomically — via a temporary file and a replace — so a
    /// crash mid-write cannot leave Steam with a truncated <c>appmanifest</c> it will refuse to load.
    /// </summary>
    public static void WriteFile(string path, KvNode root)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".steamdisc.tmp";
        // UTF-8 with no BOM: Steam does not write one, and some parsers choke on it.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(temporary, Write(root), encoding);

        if (File.Exists(path))
        {
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    private static void WriteNode(StringBuilder builder, KvNode node, int depth)
    {
        var indent = new string('\t', depth);

        if (node.IsObject)
        {
            builder.Append(indent).Append(Quote(node.Key));
            AppendCondition(builder, node);
            builder.Append(NewLine);
            builder.Append(indent).Append('{').Append(NewLine);

            foreach (var child in node.Children)
            {
                WriteNode(builder, child, depth + 1);
            }

            builder.Append(indent).Append('}').Append(NewLine);
            return;
        }

        builder.Append(indent).Append(Quote(node.Key)).Append("\t\t").Append(Quote(node.Value ?? string.Empty));
        AppendCondition(builder, node);
        builder.Append(NewLine);
    }

    private static void AppendCondition(StringBuilder builder, KvNode node)
    {
        if (!string.IsNullOrEmpty(node.Condition))
        {
            builder.Append(" [").Append(node.Condition).Append(']');
        }
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
