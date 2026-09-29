using VoiceCore.Synthetic;

// batch mode proper lands at build step 5 (spec §6): it runs VoiceCore over a WAV
// corpus through the same streaming path and emits per-frame Parquet plus a
// summary CSV. until then this only exports synthetic suite signals.
switch (args)
{
    case ["synth", "list", .. var filter]:
        foreach (var c in Suites.All().Where(c => filter.Length == 0 || c.Name.StartsWith(filter[0], StringComparison.Ordinal)))
            Console.WriteLine(c.Name);
        return 0;

    case ["synth", var name, var outPath]:
        var match = Suites.All().FirstOrDefault(c => c.Name == name);
        if (match is null)
        {
            Console.Error.WriteLine($"no suite case named '{name}'; see 'synth list'.");
            return 1;
        }
        var signal = match.Build();
        WavWriter.WriteFloat32(outPath, signal.Samples);
        Console.WriteLine($"wrote {signal.Length} samples ({signal.Length / (double)SyntheticSignal.SampleRate:0.###} s) to {outPath}");
        return 0;

    default:
        Console.Error.WriteLine("""
            usage:
              VoiceCore.Batch synth list [prefix]      list synthetic suite cases
              VoiceCore.Batch synth <case> <out.wav>   write one case as 48 kHz float WAV
            corpus batch mode arrives at build step 5.
            """);
        return 1;
}
