using System.Globalization;
using System.Text;

namespace VoiceCore.Batch.Corpus;

/// <summary>One metric on one slice of one split, with its bootstrap 95% CI.</summary>
internal sealed record SliceMetric(
    string Split, string Slice, string Metric, double Value, double CiLow, double CiHigh,
    int Files, int Speakers, long Frames, bool Qualifies, string Status);

/// <summary>
/// Run summary (spec §6): every metric per corpus slice and split, with bootstrap
/// CIs by file, the aspirational targets on qualifying slices (≥ 3 speakers and
/// ≥ 10 files), and regression gates against the last accepted run.
/// </summary>
internal sealed class CorpusReport
{
    public const int MinSpeakers = 3;
    public const int MinFiles = 10;
    public const int BootstrapResamples = 1000;

    /// <summary>Below this, a change is float noise, not a regression.</summary>
    public const double RoundingTolerance = 1e-9;
    public static readonly string[] Splits = ["dev", "heldout"];

    private readonly List<FileResult> _results;

    public CorpusReport(List<FileResult> results, Dictionary<(string Split, string Slice, string Metric), (double Value, double CiLow, double CiHigh)>? accepted = null)
    {
        _results = results;
        foreach (string split in Splits)
            foreach (var (slice, select) in Slices())
                Evaluate(split, slice, select, accepted);
    }

    public List<SliceMetric> Rows { get; } = [];

    /// <summary>Qualifying-slice target failures and regressions; empty means the run passes.</summary>
    public List<string> Failures { get; } = [];

    private IEnumerable<(string Name, Func<FileResult, FrameStats?> Select)> Slices()
    {
        yield return ("all", r => r.Total);
        foreach (string s in _results.Select(r => r.Entry.Source).Distinct().Order())
            yield return ($"source={s}", r => r.Entry.Source == s ? r.Total : null);
        foreach (string c in _results.Select(r => r.Entry.Condition).Distinct().Order())
            yield return ($"condition={c}", r => r.Entry.Condition == c ? r.Total : null);
        foreach (string t in _results.Select(r => r.Entry.Task).Distinct().Order())
            yield return ($"task={t}", r => r.Entry.Task == t ? r.Total : null);
        foreach (string band in CorpusRunner.Bands)
            yield return (band, r => r.ByBand[band]);
    }

    private void Evaluate(string split, string slice, Func<FileResult, FrameStats?> select,
        Dictionary<(string, string, string), (double Value, double CiLow, double CiHigh)>? accepted)
    {
        var files = _results.Where(r => r.Split == split)
            .Select(r => (Result: r, Stats: select(r)))
            .Where(x => x.Stats is { Frames: > 0 })
            .ToList();
        if (files.Count == 0)
            return;
        var total = new FrameStats();
        foreach (var (_, s) in files)
            total.Add(s!);
        int speakers = files.Select(x => x.Result.Entry.SpeakerId).Distinct().Count();
        bool qualifies = speakers >= MinSpeakers && files.Count >= MinFiles;

        // resample files, not frames: frames within a file are strongly correlated
        var resampled = new double[Metric.All.Length][];
        for (int m = 0; m < Metric.All.Length; m++)
            resampled[m] = new double[BootstrapResamples];
        if (files.Count >= 2)
        {
            var rng = new Random(StableSeed(split + "|" + slice));
            for (int b = 0; b < BootstrapResamples; b++)
            {
                var sample = new FrameStats();
                for (int k = 0; k < files.Count; k++)
                    sample.Add(files[rng.Next(files.Count)].Stats!);
                for (int m = 0; m < Metric.All.Length; m++)
                    resampled[m][b] = Metric.All[m].Compute(sample);
            }
        }

        for (int m = 0; m < Metric.All.Length; m++)
        {
            var metric = Metric.All[m];
            double value = metric.Compute(total);
            if (double.IsNaN(value))
                continue;
            var (lo, hi) = files.Count >= 2 ? Percentiles(resampled[m]) : (double.NaN, double.NaN);

            var status = new List<string>();
            if (Metric.Targets.TryGetValue(metric.Name, out double target))
            {
                if (!qualifies)
                    status.Add($"target {target}: not gated (slice below {MinSpeakers} speakers / {MinFiles} files)");
                else if (value < target)
                    status.Add($"target {target}: pass");
                else
                {
                    status.Add($"target {target}: FAIL");
                    Failures.Add($"{split} / {slice}: {metric.Name} {Format(value)} {metric.Unit} misses the target {target} {metric.Unit}.");
                }
            }
            if (accepted is not null && accepted.TryGetValue((split, slice, metric.Name), out var before) && !double.IsNaN(before.Value))
            {
                // no metric may worsen by more than its CI vs the last accepted run (spec §6).
                // A one-file slice has no CI (bootstrapping its correlated frames would
                // invent one), and the analyzer is deterministic on a fixed corpus, so
                // there any worsening at all fails.
                double tolerance = double.IsNaN(lo) ? RoundingTolerance : Math.Max((hi - lo) / 2, RoundingTolerance);
                double worse = metric.LowerIsBetter ? value - before.Value : before.Value - value;
                if (worse > tolerance)
                {
                    status.Add($"REGRESSED from {Format(before.Value)}{(double.IsNaN(lo) ? " (one file, no CI: any worsening fails)" : "")}");
                    Failures.Add($"{split} / {slice}: {metric.Name} worsened from {Format(before.Value)} to {Format(value)} {metric.Unit} (more than its CI half-width {Format(tolerance)}).");
                }
                else
                    status.Add($"vs accepted {Format(before.Value)}: ok");
            }
            Rows.Add(new SliceMetric(split, slice, metric.Name, value, lo, hi, files.Count, speakers, total.Frames, qualifies, string.Join("; ", status)));
        }
    }

    private static (double, double) Percentiles(double[] values)
    {
        var finite = values.Where(double.IsFinite).Order().ToArray();
        if (finite.Length == 0)
            return (double.NaN, double.NaN);
        double P(double p) => finite[(int)Math.Clamp(Math.Round(p * (finite.Length - 1)), 0, finite.Length - 1)];
        return (P(0.025), P(0.975));
    }

    /// <summary>FNV-1a, so bootstrap CIs are reproducible run to run (string.GetHashCode is randomized per process).</summary>
    private static int StableSeed(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
            h = (h ^ c) * 16777619;
        return (int)h;
    }

    public static string Format(double v) => double.IsNaN(v) ? "n/a" : v.ToString(Math.Abs(v) >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);

    public void WriteSummaryCsv(string path)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("split,slice,metric,value,ci_low,ci_high,files,speakers,frames,qualifies,status");
        string F(double v) => double.IsNaN(v) ? "" : v.ToString("R", CultureInfo.InvariantCulture);
        foreach (var r in Rows)
            w.WriteLine(Csv.Line([r.Split, r.Slice, r.Metric, F(r.Value), F(r.CiLow), F(r.CiHigh),
                r.Files.ToString(CultureInfo.InvariantCulture), r.Speakers.ToString(CultureInfo.InvariantCulture),
                r.Frames.ToString(CultureInfo.InvariantCulture), r.Qualifies ? "1" : "0", r.Status]));
    }

    public static Dictionary<(string, string, string), (double Value, double CiLow, double CiHigh)> ReadSummaryCsv(string path)
    {
        double P(string s) => s.Length == 0 ? double.NaN : double.Parse(s, CultureInfo.InvariantCulture);
        return Csv.Read(path).Rows.ToDictionary(
            r => (r["split"], r["slice"], r["metric"]),
            r => (P(r["value"]), P(r["ci_low"]), P(r["ci_high"])));
    }

    public string Markdown(string header, IEnumerable<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);

        sb.AppendLine("## Gates");
        sb.AppendLine();
        if (Failures.Count == 0)
            sb.AppendLine("No qualifying-slice target misses and no regressions.");
        foreach (string f in Failures)
            sb.AppendLine($"- {f}");
        sb.AppendLine();
        sb.AppendLine($"Targets (spec §6, aspirational): GPE < 2%, VDE < 5%. They gate only slices with ≥ {MinSpeakers} speakers and ≥ {MinFiles} files; smaller slices report bootstrap 95% CIs and are held to regression gates against the last accepted run. "
            + "One-file slices have no CI, so any worsening fails their regression gate. "
            + "FPE < 15 cents is reported, not gated: the laryngograph reference scores Praat itself at ~25 cents RMS (docs/corpus.md).");
        sb.AppendLine();

        string[] headline = ["GPE", "FPE", "median |fine|", "fine bias", "VDE", "Voiced precision", "Voiced recall", "F0Confidence ECE", "coverage@0.8", "out-of-range folds"];
        foreach (string split in Splits)
        {
            var rows = Rows.Where(r => r.Split == split).ToList();
            if (rows.Count == 0)
            {
                sb.AppendLine($"## {split}: no files");
                sb.AppendLine();
                continue;
            }
            sb.AppendLine($"## {split}");
            sb.AppendLine();
            sb.AppendLine("| slice | files | speakers | " + string.Join(" | ", headline) + " |");
            sb.AppendLine("|---|---:|---:|" + string.Concat(headline.Select(_ => "---|")));
            foreach (var group in rows.GroupBy(r => r.Slice))
            {
                var first = group.First();
                var cells = headline.Select(m => group.FirstOrDefault(r => r.Metric == m) is { } r
                    ? $"{Format(r.Value)}{(double.IsNaN(r.CiLow) ? "" : $" [{Format(r.CiLow)}–{Format(r.CiHigh)}]")}"
                    : "");
                sb.AppendLine($"| {group.Key}{(first.Qualifies ? " ✓" : "")} | {first.Files} | {first.Speakers} | {string.Join(" | ", cells)} |");
            }
            sb.AppendLine();
            sb.AppendLine("✓ = slice qualifies for target gates. Percentages except ECE; FPE and fine bias in cents. Brackets: bootstrap 95% CI by file.");
            sb.AppendLine();

            var stats = new FrameStats();
            foreach (var r in _results.Where(r => r.Split == split))
                stats.Add(r.Total);
            AppendConfusion(sb, stats);
            AppendReliability(sb, "F0Confidence (correct = not a gross error)", stats.F0Reliability);
            foreach (var state in new[] { VoicingState.Voiced, VoicingState.Unvoiced, VoicingState.Silence })
                AppendReliability(sb, $"VoicingConfidence on {state} frames (correct = reference agrees)", stats.VoicingReliability[(int)state]);
        }

        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| file | split | speaker | task | frames | GPE | FPE | VDE | noise floor |");
        sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---|");
        foreach (var r in _results.OrderBy(r => r.Split).ThenBy(r => r.Entry.File, StringComparer.Ordinal))
            sb.AppendLine($"| {r.Entry.File} | {r.Split} | {r.Entry.SpeakerId} | {r.Entry.Task} | {r.FrameCount} | "
                + $"{Format(Metric.Gpe.Compute(r.Total))} | {Format(Metric.Fpe.Compute(r.Total))} | {Format(Metric.Vde.Compute(r.Total))} | "
                + $"{r.NoiseFloorDbfs.ToString("0.0", CultureInfo.InvariantCulture)} dBFS ({r.NoiseFloorSource}) |");
        sb.AppendLine();

        var warningList = warnings.ToList();
        if (warningList.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (string w in warningList)
                sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendConfusion(StringBuilder sb, FrameStats s)
    {
        string[] columns = ["Silence", "Unvoiced", "Voiced", "Creak", "not voiced (binary)"];
        sb.AppendLine("Confusion matrix (rows: published state; columns: reference):");
        sb.AppendLine();
        sb.AppendLine("| published | " + string.Join(" | ", columns) + " |");
        sb.AppendLine("|---|" + string.Concat(columns.Select(_ => "---:|")));
        foreach (VoicingState state in Enum.GetValues<VoicingState>())
        {
            var cells = Enumerable.Range(0, 5).Select(j => s.Confusion[(int)state, j].ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"| {state} | {string.Join(" | ", cells)} |");
        }
        if (s.Excluded > 0)
            sb.AppendLine($"\n{s.Excluded} frames excluded by hand labels.");
        sb.AppendLine();
    }

    private static void AppendReliability(StringBuilder sb, string title, ReliabilityBins bins)
    {
        if (bins.Total == 0)
            return;
        sb.AppendLine($"Reliability, {title}. ECE {Format(bins.Ece())}:");
        sb.AppendLine();
        sb.AppendLine("| confidence | frames | mean predicted | observed |");
        sb.AppendLine("|---|---:|---:|---:|");
        for (int b = 0; b < ReliabilityBins.Count; b++)
        {
            if (bins.N[b] == 0)
                continue;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {b / 10.0:0.0}–{(b + 1) / 10.0:0.0} | {bins.N[b]} | {bins.SumPredicted[b] / bins.N[b]:0.000} | {bins.Correct[b] / (double)bins.N[b]:0.000} |"));
        }
        sb.AppendLine();
    }
}
