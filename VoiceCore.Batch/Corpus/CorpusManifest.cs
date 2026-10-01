using System.Globalization;
using System.Security.Cryptography;

namespace VoiceCore.Batch.Corpus;

/// <summary>One recording in the corpus manifest (spec §6 input).</summary>
internal sealed record ManifestEntry
{
    public required string SpeakerId { get; init; }
    public required string File { get; init; }
    public string Source { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Device { get; init; } = "";
    public string Os { get; init; } = "";
    public string Condition { get; init; } = "";
    public string Task { get; init; } = "";
    public string Vowel { get; init; } = "";
    public string TargetF0 { get; init; } = "";
    public string OsProcessingFlags { get; init; } = "";
    public string ConsentRef { get; init; } = "";

    /// <summary>The session's calibrated floor; null means estimate it from the file.</summary>
    public float? NoiseFloorDbfs { get; init; }

    /// <summary>Reference f0/voicing track, relative to the corpus root.</summary>
    public string Reference { get; init; } = "";

    public string Stem => Path.GetFileNameWithoutExtension(File);
}

/// <summary>
/// The local debug corpus (spec §6): manifest.csv, splits.csv, audio/,
/// reference/, labels/, consent/. It lives in corpus/, which is gitignored.
/// </summary>
internal sealed class CorpusManifest
{
    /// <summary>The spec §6 manifest columns plus noise_floor_dbfs and reference.</summary>
    public static readonly string[] Columns =
    [
        "speaker_id", "file", "source", "session_id", "device", "os", "condition", "task",
        "vowel", "target_f0", "os_processing_flags", "consent_ref", "noise_floor_dbfs", "reference",
    ];

    private readonly Dictionary<string, string> _splits;

    private CorpusManifest(string root, List<ManifestEntry> entries, Dictionary<string, string> splits, string hash)
    {
        Root = root;
        Entries = entries;
        _splits = splits;
        ManifestHash = hash;
    }

    public string Root { get; }
    public List<ManifestEntry> Entries { get; }

    /// <summary>Fingerprint of everything that's scored (see <see cref="Fingerprint"/>), so a run summary names the corpus it scored.</summary>
    public string ManifestHash { get; }

    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative);

    /// <summary>"dev" or "heldout". Splits are by speaker, never by file (spec §6). Unlisted speakers are dev.</summary>
    public string SplitOf(ManifestEntry e) => _splits.GetValueOrDefault(e.SpeakerId, "dev");

    public bool HasSplitFor(string speakerId) => _splits.ContainsKey(speakerId);

    public static CorpusManifest Load(string root)
    {
        string manifestPath = System.IO.Path.Combine(root, "manifest.csv");
        if (!System.IO.File.Exists(manifestPath))
            throw new FileNotFoundException($"no corpus manifest at {manifestPath}. See docs/corpus.md.");
        var (_, rows) = Csv.Read(manifestPath);
        var entries = new List<ManifestEntry>();
        foreach (var r in rows)
        {
            string Get(string k) => r.GetValueOrDefault(k, "").Trim();
            string floor = Get("noise_floor_dbfs");
            entries.Add(new ManifestEntry
            {
                SpeakerId = Get("speaker_id"),
                File = Get("file"),
                Source = Get("source"),
                SessionId = Get("session_id"),
                Device = Get("device"),
                Os = Get("os"),
                Condition = Get("condition"),
                Task = Get("task"),
                Vowel = Get("vowel"),
                TargetF0 = Get("target_f0"),
                OsProcessingFlags = Get("os_processing_flags"),
                ConsentRef = Get("consent_ref"),
                NoiseFloorDbfs = floor.Length > 0 ? float.Parse(floor, CultureInfo.InvariantCulture) : null,
                Reference = Get("reference"),
            });
        }

        var splits = new Dictionary<string, string>(StringComparer.Ordinal);
        string splitsPath = System.IO.Path.Combine(root, "splits.csv");
        if (System.IO.File.Exists(splitsPath))
        {
            foreach (var r in Csv.Read(splitsPath).Rows)
            {
                string split = r.GetValueOrDefault("split", "").Trim();
                if (split is not ("dev" or "heldout"))
                    throw new InvalidDataException($"splits.csv: split must be dev or heldout, was '{split}'.");
                splits[r["speaker_id"].Trim()] = split;
            }
        }

        return new CorpusManifest(root, entries, splits, Fingerprint(root, manifestPath, splitsPath, entries));
    }

    /// <summary>
    /// Identifies the scoring evidence: manifest and splits, plus every file's audio,
    /// reference and hand labels, each with its presence. Correcting a reference or
    /// adding labels changes it, so annotation work can't pass for an analyzer change
    /// in the regression gate. The analyzer is identified separately (config hash).
    /// </summary>
    private static string Fingerprint(string root, string manifestPath, string splitsPath, List<ManifestEntry> entries)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string label, string path)
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(label + "\0"));
            if (System.IO.File.Exists(path))
            {
                sha.AppendData("present\0"u8);
                sha.AppendData(System.IO.File.ReadAllBytes(path));
            }
            else
                sha.AppendData("absent\0"u8);
        }
        Add("manifest", manifestPath);
        Add("splits", splitsPath);
        foreach (var e in entries)
        {
            Add("audio:" + e.File, System.IO.Path.Combine(root, e.File));
            Add("reference:" + e.Reference, e.Reference.Length > 0 ? System.IO.Path.Combine(root, e.Reference) : "");
            string labels = System.IO.Path.Combine("labels", e.Stem + ".csv");
            Add("labels:" + labels, System.IO.Path.Combine(root, labels));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
    }

    public static void Append(string root, ManifestEntry e)
    {
        string path = System.IO.Path.Combine(root, "manifest.csv");
        bool fresh = !System.IO.File.Exists(path);
        using var w = new StreamWriter(path, append: true);
        if (fresh)
            w.WriteLine(string.Join(',', Columns));
        w.WriteLine(Csv.Line(
        [
            e.SpeakerId, e.File, e.Source, e.SessionId, e.Device, e.Os, e.Condition, e.Task,
            e.Vowel, e.TargetF0, e.OsProcessingFlags, e.ConsentRef,
            e.NoiseFloorDbfs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", e.Reference,
        ]));
    }
}
