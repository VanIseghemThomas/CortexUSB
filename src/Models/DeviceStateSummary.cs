namespace OpenCortex.CortexUSB.Models
{
    // ─── Lightweight state summary for callers (excludes heavy collections) ─────

    // Property syntax (parameterless constructor + init properties), NOT a
    // positional record: confirmed live that System.Text.Json's reflection
    // serializer throws InvalidOperationException: NullabilityInfoContext_NotSupported
    // for any type with a parameterized constructor (which every positional
    // record has) — it's specifically DetermineParameterNullability that
    // fails, not general property serialization. Tried overriding the
    // System.Reflection.NullabilityInfoContext.IsSupported runtime feature
    // switch at the WASM project level (PublishTrimmed, a
    // RuntimeHostConfigurationOption item, an explicit late-running MSBuild
    // Target) — the published runtimeconfig.json did end up showing the
    // correct value, but the live app still crashed, meaning this specific
    // WasmEnableThreads + `dotnet publish` build path bakes the feature
    // switch into the natively-relinked module some other way that ignores
    // the generated JSON entirely. Avoiding the parameterized-constructor
    // code path in our own types sidesteps the whole problem regardless of
    // platform config.
    public record DeviceStateSummary
    {
        public PresetInfo? CurrentPreset { get; init; }
        public PresetDetails? PresetDetails { get; init; }
        public int Scene { get; init; }
        public int Mode { get; init; }
        public int Bpm { get; init; }
        public List<GridRow>? Grid { get; init; }
        public DateTime Timestamp { get; init; }
        public GlobalEqState? GlobalEq { get; init; }
        public MasterVolumeState? MasterVolume { get; init; }
        public TunerState? Tuner { get; init; }
        public IoMeterState? IoMeter { get; init; }
        public CpuLoadState? CpuLoad { get; init; }

        public string ModeName => DeviceMode.GetModeName(Mode);
    }

    public record FlatPreset
    {
        public string Name { get; init; } = string.Empty;
        public int Index { get; init; }
        public string Path { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string SetlistPath { get; init; } = string.Empty;
    }
}
