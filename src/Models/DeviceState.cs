using System.Text.Json.Serialization;

namespace OpenCortex.CortexUSB.Models
{
    /// <summary>
    /// Device information (firmware, serial, etc.)
    /// Populated from Version (type 10) and Connection (type 49) messages.
    /// </summary>
    public record DeviceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "Quad Cortex";

        [JsonPropertyName("firmwareVersion")]
        public string FirmwareVersion { get; init; } = string.Empty;

        [JsonPropertyName("protocolVersion")]
        public string ProtocolVersion { get; init; } = string.Empty;

        [JsonPropertyName("serialNumber")]
        public string SerialNumber { get; init; } = string.Empty;

        [JsonPropertyName("macAddress")]
        public string MacAddress { get; init; } = string.Empty;

        [JsonPropertyName("isConnected")]
        public bool IsConnected { get; init; }
    }

    /// <summary>
    /// Current preset location information.
    /// Populated from SetlistPosition (type 2) message.
    /// </summary>
    public record PresetInfo
    {
        [JsonPropertyName("setlistPath")]
        public string SetlistPath { get; init; } = string.Empty;

        [JsonPropertyName("presetIndex")]
        public int PresetIndex { get; init; }

        [JsonPropertyName("isFactory")]
        public bool IsFactory { get; init; }
    }

    /// <summary>
    /// Current preset details (name, author, scenes).
    /// Populated from RecallPreset (type 15) message.
    /// </summary>
    public record PresetDetails
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; init; } = string.Empty;

        [JsonPropertyName("uuid")]
        public string Uuid { get; init; } = string.Empty;

        [JsonPropertyName("created")]
        public string Created { get; init; } = string.Empty;

        [JsonPropertyName("fwVersion")]
        public string FwVersion { get; init; } = string.Empty;

        [JsonPropertyName("scenes")]
        public List<string> Scenes { get; init; } = [];

        [JsonPropertyName("sceneColors")]
        public List<uint> SceneColors { get; init; } = [];
    }

    public record GridRow
    {
        [JsonPropertyName("blocks")]
        public List<Block> Blocks { get; init; } = [];

        [JsonPropertyName("input")]
        public string Input { get; init; } = string.Empty;

        [JsonPropertyName("output")]
        public string Output { get; init; } = string.Empty;

        [JsonPropertyName("inPortId")]
        public int InPortId { get; init; } = -1;

        [JsonPropertyName("outPortId")]
        public int OutPortId { get; init; } = -1;

        [JsonPropertyName("splits")]
        public List<SplitInfo> Splits { get; init; } = [];
    }

    public record SplitInfo
    {
        [JsonPropertyName("splitterSlotIndex")]
        public int SplitterSlotIndex { get; init; }

        [JsonPropertyName("mixerSlotIndex")]
        public int MixerSlotIndex { get; init; }

        [JsonPropertyName("splitterModelId")]
        public int SplitterModelId { get; init; }

        [JsonPropertyName("mixerModelId")]
        public int MixerModelId { get; init; }

        [JsonPropertyName("splitterName")]
        public string SplitterName { get; init; } = string.Empty;

        [JsonPropertyName("mixerName")]
        public string MixerName { get; init; } = string.Empty;

        [JsonPropertyName("splitterParams")]
        public List<BlockParam> SplitterParams { get; init; } = [];

        [JsonPropertyName("mixerParams")]
        public List<BlockParam> MixerParams { get; init; } = [];
    }

    public record Block
    {
        [JsonPropertyName("modelId")]
        public int ModelId { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; init; } = string.Empty;

        [JsonPropertyName("bypassed")]
        public bool Bypassed { get; init; }

        [JsonPropertyName("type")]
        public BlockType Type { get; init; } = BlockType.Normal;

        [JsonPropertyName("slotIndex")]
        public int SlotIndex { get; init; } = -1;

        [JsonPropertyName("isCapture")]
        public bool IsCapture { get; init; }

        [JsonPropertyName("params")]
        public List<BlockParam> Params { get; init; } = [];
    }

    public record BlockParam
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public float Value { get; init; }

        [JsonPropertyName("min")]
        public float Min { get; init; }

        [JsonPropertyName("max")]
        public float Max { get; init; }

        [JsonPropertyName("paramType")]
        public ParamType ParamType { get; init; } = ParamType.Unknown;
    }

    public enum ParamType
    {
        Unknown = -1,
        Float = 0,
        Int = 1,
        Switch = 2,
        RotarySwitch = 3,
        Fader = 4,
        Meter = 5,
        StereoMeter = 6,
        GrMeter = 7,
        StereoGrMeter = 8,
        String = 9,
        ToggleButton = 10,
        ComboBox = 11,
        FloatWithLed = 12,
        Empty = 13
    }

    public enum BlockType
    {
        Normal,
        Split,
        Merge
    }

    public record ModelInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; init; } = string.Empty;

        [JsonPropertyName("paramDefs")]
        public List<ParamDef> ParamDefs { get; init; } = [];
    }

    public record ParamDef
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("min")]
        public float Min { get; init; }

        [JsonPropertyName("max")]
        public float Max { get; init; }

        [JsonPropertyName("paramType")]
        public ParamType ParamType { get; init; } = ParamType.Unknown;
    }

    public record PresetDirectory
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("presets")]
        public List<PresetEntry> Presets { get; init; } = [];

        [JsonPropertyName("children")]
        public List<PresetDirectory> Children { get; init; } = [];
    }

    public record PresetEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; init; } = string.Empty;

        [JsonPropertyName("uuid")]
        public string Uuid { get; init; } = string.Empty;
    }

    /// <summary>
    /// One Global EQ band (1-5, the unit's own numbering). Values are the
    /// normalized 0..1 the wire carries; gain 0.5=0dB, 0.75=+6dB.
    /// </summary>
    public record GlobalEqBand
    {
        [JsonPropertyName("band")]
        public int Band { get; init; }

        [JsonPropertyName("gain")]
        public float Gain { get; init; } = 0.5f;

        [JsonPropertyName("frequency")]
        public float Frequency { get; init; }

        [JsonPropertyName("q")]
        public float Q { get; init; } = 0.5f;

        [JsonPropertyName("filterType")]
        public float FilterType { get; init; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Global EQ state. Populated from GlobalEQ (type 38) pushes.
    /// </summary>
    public record GlobalEqState
    {
        [JsonPropertyName("bypassed")]
        public bool Bypassed { get; init; }

        [JsonPropertyName("outputLevel")]
        public float OutputLevel { get; init; } = 0.5f;

        [JsonPropertyName("out12")]
        public bool Out12 { get; init; } = true;

        [JsonPropertyName("out34")]
        public bool Out34 { get; init; }

        [JsonPropertyName("bands")]
        public List<GlobalEqBand> Bands { get; init; } = [];
    }

    /// <summary>
    /// Master volume state. Populated from MasterVolume (type 17) pushes.
    /// </summary>
    public record MasterVolumeState
    {
        [JsonPropertyName("volume")]
        public float Volume { get; init; } = 1.0f;
    }

    /// <summary>
    /// Tuner state. Populated from Tuner (type 6) pushes.
    ///
    /// WARNING: any write to the Tuner subsystem invisibly engages it — nothing
    /// changes on screen. If <see cref="Mute"/> is true, the outputs go silent
    /// with no visible indication; the only lossless release is a person opening
    /// and closing the tuner on the unit. See ProtocolService.RestoreAudioAsync.
    /// </summary>
    public record TunerState
    {
        [JsonPropertyName("inputPortId")]
        public int InputPortId { get; init; } = -1;

        [JsonPropertyName("mute")]
        public bool Mute { get; init; }

        [JsonPropertyName("frequency")]
        public float Frequency { get; init; }
    }

    /// <summary>
    /// Complete device state snapshot.
    /// This is what gets sent to WebSocket clients.
    /// </summary>
    public record DeviceState
    {
        [JsonPropertyName("deviceInfo")]
        public DeviceInfo? DeviceInfo { get; init; }

        [JsonPropertyName("currentPreset")]
        public PresetInfo? CurrentPreset { get; init; }

        [JsonPropertyName("presetDetails")]
        public PresetDetails? PresetDetails { get; init; }

        [JsonPropertyName("scene")]
        public int Scene { get; init; }

        [JsonPropertyName("mode")]
        public int Mode { get; init; }

        [JsonPropertyName("bpm")]
        public int Bpm { get; init; }

        [JsonPropertyName("grid")]
        public List<GridRow> Grid { get; init; } = [];

        [JsonPropertyName("modelMap")]
        public Dictionary<int, ModelInfo> ModelMap { get; init; } = [];

        [JsonPropertyName("presetLibrary")]
        public List<PresetDirectory> PresetLibrary { get; init; } = [];

        [JsonPropertyName("globalEq")]
        public GlobalEqState GlobalEq { get; init; } = new();

        [JsonPropertyName("masterVolume")]
        public MasterVolumeState MasterVolume { get; init; } = new();

        [JsonPropertyName("tuner")]
        public TunerState Tuner { get; init; } = new();

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// State update event with change source tracking.
    /// Used for broadcasting updates to WebSocket clients.
    /// </summary>
    public record StateUpdate
    {
        [JsonPropertyName("state")]
        public DeviceState State { get; init; } = new();

        [JsonPropertyName("changedBy")]
        public string ChangedBy { get; init; } = "unknown";

        [JsonPropertyName("changeType")]
        public string ChangeType { get; init; } = "full";

        public static StateUpdate FromDevice(DeviceState state) => new()
        {
            State = state,
            ChangedBy = "device",
            ChangeType = "full"
        };

        public static StateUpdate FromClient(DeviceState state, string changeType) => new()
        {
            State = state,
            ChangedBy = "client",
            ChangeType = changeType
        };
    }

    /// <summary>
    /// Constants for message types.
    /// Matches CortexMessageType enum from the protocol.
    /// </summary>
    public static class MessageTypes
    {
        public const uint Grid = 1;
        public const uint SetlistPosition = 2;
        public const uint File = 4;
        public const uint IOMeter = 5;
        public const uint Tuner = 6;
        public const uint Version = 10;
        public const uint Scene = 13;
        public const uint Mode = 14;
        public const uint RecallPreset = 15;
        public const uint MasterVolume = 17;
        public const uint SceneCopy = 22;
        public const uint SceneLabel = 23;
        public const uint ShowTuner = 27;
        public const uint KeepAlive = 32;
        public const uint Tempo = 33;
        public const uint PresetDirty = 34; // Device→Host - Preset has unsaved changes
        public const uint GlobalEQ = 38;
        public const uint SceneColor = 48;
        public const uint Connection = 49;
        public const uint NewModels = 50; // Device→Host - new model IDs registered (e.g. a Capture was saved); re-fetch ModelRepo
        public const uint ModelRepo = 51;
        public const uint ResetComms = 52;
    }

    /// <summary>
    /// Mode constants for device mode (Preset/Scene/Stomp).
    /// </summary>
    public static class DeviceMode
    {
        public const int Preset = 0;
        public const int Scene = 1;
        public const int Stomp = 2;

        /// <summary>Device firmware uses 6 for Stomp mode on newer QC units.</summary>
        public const int StompV2 = 6;

        public static string GetModeName(int mode) => mode switch
        {
            Preset => "Preset",
            Scene => "Scene",
            Stomp or StompV2 => "Stomp",
            _ => $"Unknown({mode})"
        };
    }
}
