namespace CortexUSB.Models
{
    // ─── Lightweight state summary for callers (excludes heavy collections) ─────

    public record DeviceStateSummary(
        PresetInfo? CurrentPreset,
        PresetDetails? PresetDetails,
        int Scene,
        int Mode,
        int Bpm,
        List<GridRow>? Grid,
        DateTime Timestamp,
        GlobalEqState? GlobalEq = null,
        MasterVolumeState? MasterVolume = null,
        TunerState? Tuner = null
    )
    {
        public string ModeName => DeviceMode.GetModeName(Mode);
    };

    public record FlatPreset(string Name, int Index, string Path, string Author, string SetlistPath);
}
