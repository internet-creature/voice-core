using Parquet;
using Parquet.Schema;

namespace VoiceCore.Batch.Corpus;

/// <summary>
/// Per-file batch output (spec §6): one row per frame on the §4 frame schema,
/// plus the reference the frame was scored against. Analyzer version, config
/// hash and calibration go in the file metadata once (§4), never in rows.
/// NaN stays NaN ("not measured").
/// </summary>
internal static class ParquetFrames
{
    public static void Write(string path, ReadOnlySpan<AnalysisFrame> frames, RefPoint[] refs, AnalysisConfig config,
        Dictionary<string, string> extraMetadata)
    {
        var f = frames.ToArray();
        var schemaFields = new List<DataField>();
        var writes = new List<Func<ParquetRowGroupWriter, Task>>();

        void Add<T>(string name, Func<AnalysisFrame, int, T> value) where T : struct
        {
            var field = new DataField<T>(name);
            schemaFields.Add(field);
            var data = new T[f.Length];
            for (int i = 0; i < f.Length; i++)
                data[i] = value(f[i], i);
            writes.Add(rg => rg.WriteAsync<T>(field, data));
        }

        Add("window_center_sample", (x, _) => x.WindowCenterSample);
        Add("result_available_sample", (x, _) => x.ResultAvailableSample);
        Add("time_s", (x, _) => x.TimeSeconds);
        Add("voicing", (x, _) => (int)x.Voicing);
        Add("voicing_confidence", (x, _) => x.VoicingConfidence);
        Add("f0_raw_hz", (x, _) => x.F0RawHz);
        Add("f0_hz", (x, _) => x.F0Hz);
        Add("f0_display_hz", (x, _) => x.F0DisplayHz);
        Add("f0_cents", (x, _) => x.F0Cents);
        Add("f0_confidence", (x, _) => x.F0Confidence);
        Add("f0_range", (x, _) => (int)x.F0Range);
        Add("aperiodicity", (x, _) => x.Aperiodicity);
        Add("rms_dbfs", (x, _) => x.RmsDbfs);
        Add("peak_dbfs", (x, _) => x.PeakDbfs);
        Add("clipping", (x, _) => x.Clipping);
        Add("f1_hz", (x, _) => x.F1Hz);
        Add("b1_hz", (x, _) => x.B1Hz);
        Add("f2_hz", (x, _) => x.F2Hz);
        Add("b2_hz", (x, _) => x.B2Hz);
        Add("f3_hz", (x, _) => x.F3Hz);
        Add("b3_hz", (x, _) => x.B3Hz);
        Add("f4_hz", (x, _) => x.F4Hz);
        Add("b4_hz", (x, _) => x.B4Hz);
        Add("formant_confidence", (x, _) => x.FormantConfidence);
        Add("cpp_db", (x, _) => x.CppDb);
        Add("brightness_proxy", (x, _) => x.BrightnessProxy);
        Add("spectral_tilt_db_per_khz", (x, _) => x.SpectralTiltDbPerKhz);
        Add("ref_state", (_, i) => (int)refs[i].State);
        Add("ref_f0_hz", (_, i) => refs[i].F0Hz);

        var metadata = new Dictionary<string, string>(extraMetadata)
        {
            ["analyzer_version"] = config.AnalyzerVersion,
            ["config_hash"] = config.ComputeContentHash(),
            ["confidence_calibration"] = config.ConfidenceCalibration,
            ["enums"] = "voicing: 0 Silence, 1 Unvoiced, 2 Voiced, 3 Creak; f0_range: 0 In, 1 Above, 2 Below; "
                + "ref_state: 0 Silence, 1 Unvoiced (binary references: not voiced), 2 Voiced, 3 Creak, 4 Exclude, 5 no reference",
        };

        WriteAsync(path, new ParquetSchema(schemaFields), writes, metadata).GetAwaiter().GetResult();
    }

    private static async Task WriteAsync(string path, ParquetSchema schema, List<Func<ParquetRowGroupWriter, Task>> writes,
        Dictionary<string, string> metadata)
    {
        await using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        writer.CustomMetadata = metadata;
        using var rowGroup = writer.CreateRowGroup();
        foreach (var write in writes)
            await write(rowGroup);
    }
}
