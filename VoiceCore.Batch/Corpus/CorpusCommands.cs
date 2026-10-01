using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace VoiceCore.Batch.Corpus;

/// <summary>The <c>corpus</c> subcommands (spec §6, build step 5). See docs/corpus.md.</summary>
internal static class CorpusCommands
{
    public static int Run(string[] args)
    {
        string root = Option(args, "--corpus") ?? DefaultCorpusRoot();
        switch (args)
        {
            case ["add", var wav, ..]:
                return Add(root, wav, args);
            case ["run", ..]:
                return RunCorpus(root, gate: args.Contains("--gate"));
            case ["accept", ..]:
                return Accept(root, args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
            case ["calibrate", ..]:
                return Calibrate(root, Option(args, "--out") ?? Path.Combine(RepoRoot() ?? ".", "VoiceCore", "FittedCalibration.cs"));
            default:
                Console.Error.WriteLine("""
                    usage (corpus defaults to ./corpus at the repo root; --corpus <dir> overrides):
                      corpus add <rec.wav> --task <task> [--speaker self] [--condition <c>] [--vowel <v>] [--target-f0 <hz>]
                                       copy a probe recording (and its .txt sidecar) into the corpus manifest
                      corpus run [--gate]    analyze every file, score it against its reference, write
                                             corpus/runs/<time>/ (report.md, summary.csv, frames/*.parquet).
                                             --gate: exit 1 on a qualifying-slice target miss or a regression
                      corpus accept [<run>]  make a run (default: the latest) the regression baseline
                      corpus calibrate [--out <file>]
                                             fit the §3.9 confidence calibration on the dev split and write
                                             it as VoiceCore/FittedCalibration.cs
                    """);
                return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VoiceCore.slnx")))
                return dir.FullName;
        return null;
    }

    private static string DefaultCorpusRoot() => Path.Combine(RepoRoot() ?? Directory.GetCurrentDirectory(), "corpus");

    // --- add ---

    private static int Add(string root, string wavPath, string[] args)
    {
        string? task = Option(args, "--task");
        if (task is null)
        {
            Console.Error.WriteLine("--task is required (e.g. sustained, siren, read, breathy, creak; spec §6 corpus tasks).");
            return 1;
        }
        var wav = WavReader.Read(wavPath);
        if (wav.SampleRate != VoiceAnalyzer.SampleRate)
        {
            Console.Error.WriteLine($"{wavPath}: {wav.SampleRate} Hz; the corpus takes 48 kHz.");
            return 1;
        }

        Directory.CreateDirectory(Path.Combine(root, "audio"));
        Directory.CreateDirectory(Path.Combine(root, "consent"));
        string name = Path.GetFileName(wavPath);
        string dest = Path.Combine(root, "audio", name);
        if (File.Exists(dest))
        {
            Console.Error.WriteLine($"{dest} is already in the corpus.");
            return 1;
        }
        File.Copy(wavPath, dest);
        var sidecar = ReadSidecar(Path.ChangeExtension(wavPath, ".txt"));
        if (sidecar.Count > 0)
            File.Copy(Path.ChangeExtension(wavPath, ".txt"), Path.ChangeExtension(dest, ".txt"));

        string speaker = Option(args, "--speaker") ?? "self";
        string consent = Option(args, "--consent") ?? $"consent/{speaker}.md";
        if (speaker == "self" && !File.Exists(Path.Combine(root, consent)))
            File.WriteAllText(Path.Combine(root, consent), $"""
                # Developer's own recordings

                - speaker: the developer (speaker_id "self")
                - consent: given {DateTime.Now:yyyy-MM-dd} for keeping probe recordings in the local debug corpus
                - use: development measurement only (metrics, confidence calibration). Local only; never committed.
                - retention: until deleted
                - removal: delete the self files from audio/, reference/ and labels/, and their manifest rows
                """);

        string device = sidecar.GetValueOrDefault("device", "");
        string started = sidecar.GetValueOrDefault("started", "");
        string session = DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? $"{speaker}-{t:yyyyMMdd}" : speaker;
        float? floor = float.TryParse(sidecar.GetValueOrDefault("noise_floor_dbfs", ""), CultureInfo.InvariantCulture, out float f) ? f : null;
        string flags = sidecar.GetValueOrDefault("raw", "") switch
        {
            "True" => "off (raw capture)",
            "False" => "unknown (raw capture not granted)",
            _ => "unknown",
        };

        CorpusManifest.Append(root, new ManifestEntry
        {
            SpeakerId = speaker,
            File = $"audio/{name}",
            Source = speaker == "self" ? "self" : Option(args, "--source") ?? "volunteer",
            SessionId = session,
            Device = device,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Condition = Option(args, "--condition") ?? "home",
            Task = task,
            Vowel = Option(args, "--vowel") ?? "",
            TargetF0 = Option(args, "--target-f0") ?? "",
            OsProcessingFlags = flags,
            ConsentRef = consent,
            NoiseFloorDbfs = floor,
        });
        // a new speaker is dev until someone decides otherwise (one person can't be on both sides of the split)
        string splits = Path.Combine(root, "splits.csv");
        if (!File.Exists(splits) || !Csv.Read(splits).Rows.Any(r => r.GetValueOrDefault("speaker_id") == speaker))
            File.AppendAllText(splits, (File.Exists(splits) ? "" : "speaker_id,split\n") + $"{Csv.Escape(speaker)},dev\n");

        Console.WriteLine($"added audio/{name} ({wav.Samples.Length / (double)VoiceAnalyzer.SampleRate:0.0} s, task {task}).");
        Console.WriteLine("next: tools/.venv/Scripts/python tools/praat_reference.py   (writes its Praat reference)");
        return 0;
    }

    private static Dictionary<string, string> ReadSidecar(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return values;
        foreach (string line in File.ReadLines(path))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return values;
    }

    // --- run ---

    private static (List<FileResult> Results, List<string> Warnings) AnalyzeAll(
        CorpusManifest corpus, AnalysisConfig config, Func<ManifestEntry, bool> include, string? framesDir,
        Action<FileResult, AnalysisFrame, RefPoint>? onFrame = null)
    {
        var results = new ConcurrentBag<FileResult>();
        var warnings = new ConcurrentQueue<string>();
        Parallel.ForEach(corpus.Entries.Where(include), e =>
        {
            var runner = new CorpusRunner(corpus, config) { FramesDirectory = framesDir, OnFrame = onFrame };
            if (runner.Run(e) is { } r)
                results.Add(r);
            foreach (string w in runner.Warnings)
                warnings.Enqueue(w);
        });
        foreach (string speaker in corpus.Entries.Select(e => e.SpeakerId).Distinct().Where(s => !corpus.HasSplitFor(s)))
            warnings.Enqueue($"speaker '{speaker}' has no row in splits.csv; counted as dev.");
        return (results.OrderBy(r => r.Entry.File, StringComparer.Ordinal).ToList(), warnings.Order(StringComparer.Ordinal).ToList());
    }

    private static int RunCorpus(string root, bool gate)
    {
        var corpus = CorpusManifest.Load(root);
        var config = AnalysisConfig.Default;
        string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string runDir = Path.Combine(root, "runs", runId);
        string framesDir = Path.Combine(runDir, "frames");
        Directory.CreateDirectory(framesDir);

        var (results, warnings) = AnalyzeAll(corpus, config, _ => true, framesDir);
        if (results.Count == 0)
        {
            Console.Error.WriteLine("no files could be scored; see warnings:\n  " + string.Join("\n  ", warnings));
            return 1;
        }

        string acceptedPath = Path.Combine(root, "accepted", "summary.csv");
        var accepted = File.Exists(acceptedPath) ? CorpusReport.ReadSummaryCsv(acceptedPath) : null;
        var report = new CorpusReport(results, accepted);
        report.WriteSummaryCsv(Path.Combine(runDir, "summary.csv"));
        string acceptedNote = accepted is null ? "none yet (`corpus accept` sets one)"
            : File.ReadAllText(Path.Combine(root, "accepted", "run.txt")).Trim();
        File.WriteAllText(Path.Combine(runDir, "report.md"), report.Markdown(Header(runId, corpus, config, results, acceptedNote), warnings));

        Console.WriteLine($"{results.Count} files scored -> {runDir}");
        foreach (string split in CorpusReport.Splits)
        {
            var all = report.Rows.Where(r => r.Split == split && r.Slice == "all").ToDictionary(r => r.Metric);
            if (all.Count == 0)
                continue;
            string M(string name) => all.TryGetValue(name, out var r) ? CorpusReport.Format(r.Value) : "n/a";
            Console.WriteLine($"  {split,-8} {all["VDE"].Files,3} files, {all["VDE"].Speakers,2} speakers: GPE {M("GPE")}%  FPE {M("FPE")} cents  VDE {M("VDE")}%  coverage@0.8 {M("coverage@0.8")}%  F0 ECE {M("F0Confidence ECE")}");
        }
        foreach (string f in report.Failures)
            Console.WriteLine($"  gate: {f}");
        foreach (string w in warnings)
            Console.WriteLine($"  warning: {w}");
        return gate && report.Failures.Count > 0 ? 1 : 0;
    }

    private static string Header(string runId, CorpusManifest corpus, AnalysisConfig config, List<FileResult> results, string accepted)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Corpus run {runId}");
        sb.AppendLine();
        sb.AppendLine($"- analyzer {config.AnalyzerVersion}, config hash `{config.ComputeContentHash()[..16]}`, confidence calibration: {config.ConfidenceCalibration}");
        sb.AppendLine($"- corpus manifest + splits hash `{corpus.ManifestHash}`; regression baseline: {accepted}");
        foreach (string split in CorpusReport.Splits)
        {
            var inSplit = results.Where(r => r.Split == split).ToList();
            sb.AppendLine($"- {split}: {inSplit.Count} files, {inSplit.Select(r => r.Entry.SpeakerId).Distinct().Count()} speakers"
                + (inSplit.Count > 0 ? $" ({string.Join(", ", inSplit.GroupBy(r => r.Entry.Source).Select(g => $"{g.Count()} {g.Key}"))})" : ""));
        }
        sb.AppendLine("- references (spec §6 layers; Praat is a versioned comparison baseline, not ground truth):");
        foreach (var g in results.GroupBy(r => r.ReferenceSource).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"  - {g.Count()} files: {g.Key}");
        sb.AppendLine("- causal live output only (the analyzer's streaming path). No centered-offline column yet: that needs §3.5 (step 6).");
        sb.AppendLine();
        return sb.ToString();
    }

    // --- accept ---

    private static int Accept(string root, string? runId)
    {
        string runs = Path.Combine(root, "runs");
        runId ??= Directory.Exists(runs) ? Directory.GetDirectories(runs).Select(Path.GetFileName).Order(StringComparer.Ordinal).LastOrDefault() : null;
        string summary = Path.Combine(runs, runId ?? "", "summary.csv");
        if (runId is null || !File.Exists(summary))
        {
            Console.Error.WriteLine("no such run; `corpus run` first.");
            return 1;
        }
        string accepted = Path.Combine(root, "accepted");
        Directory.CreateDirectory(accepted);
        File.Copy(summary, Path.Combine(accepted, "summary.csv"), overwrite: true);
        File.WriteAllText(Path.Combine(accepted, "run.txt"), $"run {runId}, accepted {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"run {runId} is now the regression baseline.");
        return 0;
    }

    // --- calibrate ---

    private static int Calibrate(string root, string outPath)
    {
        var corpus = CorpusManifest.Load(root);
        var raw = AnalysisConfig.Default with { Calibration = null };
        var f0 = new List<(float, bool)>();
        var voicing = new[] { new List<(float, bool)>(), new List<(float, bool)>(), new List<(float, bool)>(), new List<(float, bool)>() };
        var gate = new object();

        // dev split only: held-out is scored untouched (spec §6)
        var (results, warnings) = AnalyzeAll(corpus, raw, e => corpus.SplitOf(e) == "dev", framesDir: null, (_, frame, r) =>
        {
            bool? f0Right = FrameStats.F0Right(frame, r, raw.F0SearchMinHz, raw.F0SearchMaxHz);
            bool? stateRight = float.IsNaN(frame.VoicingConfidence) ? null : FrameStats.StateRight(frame, r);
            if (f0Right is null && stateRight is null)
                return;
            lock (gate)
            {
                if (f0Right is { } a)
                    f0.Add((frame.F0Confidence, a));
                if (stateRight is { } b)
                    voicing[(int)frame.Voicing].Add((frame.VoicingConfidence, b));
            }
        });
        foreach (string w in warnings)
            Console.WriteLine($"warning: {w}");

        var f0Map = CalibrationFit.Fit(f0);
        if (f0Map is null)
        {
            Console.Error.WriteLine($"only {f0.Count} judged f0 frames on dev; need {CalibrationFit.MinSamples}.");
            return 1;
        }
        string name = $"corpus-{DateTime.Now:yyyyMMdd}-{corpus.ManifestHash[..8]}";
        var table = new ConfidenceCalibrationTable
        {
            Name = name,
            F0 = f0Map,
            Silence = CalibrationFit.Fit(voicing[(int)VoicingState.Silence]),
            Unvoiced = CalibrationFit.Fit(voicing[(int)VoicingState.Unvoiced]),
            Voiced = CalibrationFit.Fit(voicing[(int)VoicingState.Voiced]),
            Creak = CalibrationFit.Fit(voicing[(int)VoicingState.Creak]),
        };

        var provenance = new List<string>
        {
            $"fit {DateTime.Now:yyyy-MM-dd} on the dev split of corpus {corpus.ManifestHash}: {results.Count} files, "
                + $"{results.Select(r => r.Entry.SpeakerId).Distinct().Count()} speakers ({string.Join(", ", results.GroupBy(r => r.Entry.Source).Select(g => $"{g.Count()} {g.Key}"))})",
            $"raw scores from analyzer {raw.AnalyzerVersion}; judged frames: f0 {f0.Count}, "
                + string.Join(", ", Enum.GetValues<VoicingState>().Select(s => $"{s} {voicing[(int)s].Count}")),
        };
        foreach (var g in results.GroupBy(r => r.ReferenceSource).OrderBy(g => g.Key, StringComparer.Ordinal))
            provenance.Add($"reference ({g.Count()} files): {g.Key}");
        File.WriteAllText(outPath, CalibrationFit.GenerateSource(table, provenance));

        Console.WriteLine($"wrote {outPath} ({name})");
        Console.WriteLine($"  F0Confidence: {f0.Count} judged frames, {f0Map.Raw.Count} knots");
        foreach (var s in Enum.GetValues<VoicingState>())
            Console.WriteLine($"  VoicingConfidence on {s}: {voicing[(int)s].Count} judged frames, "
                + (table.VoicingFor(s) is { } m ? $"{m.Raw.Count} knots" : "not calibrated (stays raw)"));
        Console.WriteLine("next: bump AnalysisConfig.AnalyzerVersion, rebuild, and `corpus run` to score held-out.");
        return 0;
    }
}
