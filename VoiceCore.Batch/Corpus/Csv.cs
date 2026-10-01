using System.Text;

namespace VoiceCore.Batch.Corpus;

/// <summary>
/// Minimal CSV for the corpus files: comma-separated, optional double quotes,
/// lines starting with '#' are comments (header provenance), first row is the
/// header.
/// </summary>
internal static class Csv
{
    public static (List<string> Comments, List<Dictionary<string, string>> Rows) Read(string path)
    {
        var comments = new List<string>();
        var rows = new List<Dictionary<string, string>>();
        string[]? header = null;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                comments.Add(line[1..].Trim());
                continue;
            }
            if (line.Length == 0)
                continue;
            var fields = Split(line);
            if (header is null)
            {
                header = fields.Select(f => f.Trim()).ToArray();
                continue;
            }
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < header.Length; i++)
                row[header[i]] = i < fields.Count ? fields[i] : "";
            rows.Add(row);
        }
        return (comments, rows);
    }

    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    sb.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
                sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public static string Escape(string field) =>
        field.AsSpan().IndexOfAny(",\"\n") >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;

    public static string Line(IEnumerable<string> fields) => string.Join(',', fields.Select(Escape));
}
