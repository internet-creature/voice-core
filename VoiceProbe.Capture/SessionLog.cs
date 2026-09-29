using System.Globalization;
using System.Runtime.InteropServices;
using VoiceCore;

namespace VoiceProbe.Capture;

/// <summary>
/// Per-session log (spec §2, §3): device, native format, OS, whether OS processing
/// could be disabled, analyzer version and diagnostics. Never audio, frames or
/// voice measurements (spec §0). Plain "key: value" lines so it diffs and greps.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;

    private SessionLog(string path)
    {
        Path = path;
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public string Path { get; }

    public static SessionLog Create(string directory)
    {
        Directory.CreateDirectory(directory);
        string name = $"session-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";
        var log = new SessionLog(System.IO.Path.Combine(directory, name));
        log.Write("started", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        log.Write("os", RuntimeInformation.OSDescription);
        log.Write("runtime", RuntimeInformation.FrameworkDescription);
        return log;
    }

    public void Write(string key, object? value) =>
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key}: {value}"));

    public void WriteAnalyzer(AnalysisConfig config)
    {
        Write("analyzer.version", config.AnalyzerVersion);
        Write("analyzer.config_hash", config.ComputeContentHash());
        Write("analyzer.confidence_calibration", config.ConfidenceCalibration);
    }

    public void WriteDiagnostics(string prefix, AnalyzerDiagnostics d)
    {
        Write($"{prefix}.frames_produced", d.FramesProduced);
        Write($"{prefix}.overruns", d.OverrunCount);
        Write($"{prefix}.dropped_samples", d.DroppedSamples);
        Write($"{prefix}.frame_queue_overruns", d.FrameQueueOverruns);
        Write($"{prefix}.max_analysis_ms", d.MaxAnalysisTimePerFrame.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture));
        Write($"{prefix}.capture_to_result.count", d.CaptureToResultCount);
        foreach (int p in new[] { 50, 95, 99 })
            Write($"{prefix}.capture_to_result.p{p}_ms", FormatMs(d.CaptureToResultPercentile(p)));
        Write($"{prefix}.capture_to_result.max_ms", FormatMs(d.CaptureToResultMax));
    }

    public void Dispose()
    {
        Write("ended", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        _writer.Dispose();
    }

    private static string FormatMs(TimeSpan t) =>
        t == TimeSpan.MaxValue ? ">100" : t.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture);
}
