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

        /// <summary>Which grid cells drive which Stomp-mode footswitch. A footswitch may drive several cells.</summary>
        [JsonPropertyName("stompAssignments")]
        public List<StompAssignment> StompAssignments { get; init; } = [];

        /// <summary>Per-footswitch label/momentary state (A-H, index 0-7), sparse — a missing index means unlabeled/latching.</summary>
        [JsonPropertyName("footswitches")]
        public List<FootswitchInfo> Footswitches { get; init; } = [];
    }

    /// <summary>
    /// A grid cell bound to a Stomp-mode footswitch.
    /// Populated from BinaryPreset.stomp_mode_assignments via Grid (type 1) / RecallPreset (type 15).
    /// </summary>
    public record StompAssignment
    {
        [JsonPropertyName("row")]
        public int Row { get; init; }

        [JsonPropertyName("column")]
        public int Column { get; init; }

        /// <summary>0-7 (A-H).</summary>
        [JsonPropertyName("footswitch")]
        public int Footswitch { get; init; }
    }

    /// <summary>
    /// Per-preset label and momentary/latching state for one Stomp-mode footswitch (0-7, A-H).
    /// Populated from BinaryPreset.stomp_labels/single_stomp_labels/stomp_is_momentary.
    /// </summary>
    public record FootswitchInfo
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("label")]
        public string Label { get; init; } = string.Empty;

        /// <summary>Set only when the footswitch drives exactly one block — see BuildStompLabelMessage.</summary>
        [JsonPropertyName("singleLabel")]
        public string SingleLabel { get; init; } = string.Empty;

        [JsonPropertyName("momentary")]
        public bool Momentary { get; init; }
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

        /// <summary>
        /// The manual's "SCENE ASSIGNMENTS" feature: "tap and hold a parameter to
        /// assign or unassign it to Scenes. Once assigned, the parameter's value
        /// will be stored independently for each Scene." True when the device
        /// carries more than one ParamValue for this parameter (one per scene, A-H).
        /// </summary>
        [JsonPropertyName("sceneAssigned")]
        public bool SceneAssigned { get; init; }

        /// <summary>
        /// The 8 per-scene values (A-H, index 0-7) when <see cref="SceneAssigned"/>
        /// is true; empty otherwise. <see cref="Value"/> above is always just
        /// SceneValues[currentScene] (or the single shared value when not
        /// scene-assigned) - this is the full array, for editing every scene's
        /// value rather than only whichever one is currently active.
        /// </summary>
        [JsonPropertyName("sceneValues")]
        public List<float> SceneValues { get; init; } = [];
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

        /// <summary>
        /// The device's own "based on" attribution for modeled gear (e.g. "Based on
        /// Ibanez® TS808®"), taken from the ModelRepo XML's <c>tm</c> attribute. Empty
        /// for models that aren't based on a specific piece of hardware.
        /// </summary>
        [JsonPropertyName("basedOn")]
        public string BasedOn { get; init; } = string.Empty;

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

        /// <summary>
        /// The manual's "LIVE TUNER" toggle (Tuner menu -> LIVE TUNER: "Toggles
        /// the LIVE TUNER in Gig View" - a moving pitch indicator, confirmed by
        /// the user to keep updating even away from the Tuner/Gig View screens,
        /// not just while looking at it).
        ///
        /// CORRECTED FINDING (previously "confirmed not writable" - that was
        /// wrong): a real USB capture of the official app shows it writes
        /// {action=Update, request_id, enable_meter=true} to turn this on, and
        /// <see cref="Meter"/> starts streaming real Hz values right after. The
        /// earlier refusal conclusion was a measurement artifact - the device
        /// never echoes enable_meter back on any later message (not even the
        /// immediate Read response), so the old code's echo-wait always timed
        /// out and looked like a refusal. See
        /// ProtocolService.SetTunerMeterEnabled /
        /// ProtobufBuilder.BuildTunerMeterEnableMessage for the write path, and
        /// ProtocolService.HandleTunerMessage for why this field is inferred
        /// from a meter value arriving at all rather than trusted only when the
        /// device explicitly asserts it (which it usually doesn't).
        /// </summary>
        [JsonPropertyName("enableMeter")]
        public bool EnableMeter { get; init; }

        /// <summary>
        /// The live pitch reading LIVE TUNER drives (see <see cref="EnableMeter"/>).
        /// CONFIRMED via a real USB capture: this is a real, continuously-updating
        /// frequency in Hz (observed sweeping through plausible guitar-string
        /// values, e.g. ~77-330 Hz, while a string was being tuned) - not an
        /// unconfirmed/never-observed field as earlier assumed.
        /// </summary>
        [JsonPropertyName("meter")]
        public float Meter { get; init; }
    }

    /// <summary>
    /// I/O level meters. Populated from IOMeter (type 5) pushes.
    ///
    /// UNCONFIRMED against hardware: MessageTypes.IOMeter was previously a
    /// declared-but-dead constant (nothing ever sent or subscribed to it). This
    /// mirrors the GlobalEQ/MasterVolume/Tuner "push-only field" pattern (see
    /// ProtocolService.RequestGlobalControlsRefresh) - a single Read query is
    /// sent once and the device is assumed to keep pushing updates on its own
    /// from there. Verify on the unit that levels actually keep moving after
    /// the initial request.
    ///
    /// Field names/units match the wire message directly (0.0-1.0 range,
    /// presumed linear amplitude rather than dB - unconfirmed).
    /// </summary>
    public record IoMeterState
    {
        [JsonPropertyName("input1")]
        public float Input1 { get; init; }

        [JsonPropertyName("input2")]
        public float Input2 { get; init; }

        [JsonPropertyName("return1")]
        public float Return1 { get; init; }

        [JsonPropertyName("return2")]
        public float Return2 { get; init; }

        [JsonPropertyName("xlr1")]
        public float Xlr1 { get; init; }

        [JsonPropertyName("xlr1Limiter")]
        public float Xlr1Limiter { get; init; }

        [JsonPropertyName("xlr2")]
        public float Xlr2 { get; init; }

        [JsonPropertyName("xlr2Limiter")]
        public float Xlr2Limiter { get; init; }

        [JsonPropertyName("out3")]
        public float Out3 { get; init; }

        [JsonPropertyName("out3Limiter")]
        public float Out3Limiter { get; init; }

        [JsonPropertyName("out4")]
        public float Out4 { get; init; }

        [JsonPropertyName("out4Limiter")]
        public float Out4Limiter { get; init; }

        [JsonPropertyName("send1")]
        public float Send1 { get; init; }

        [JsonPropertyName("send2")]
        public float Send2 { get; init; }

        [JsonPropertyName("hpL")]
        public float HpL { get; init; }

        [JsonPropertyName("hpR")]
        public float HpR { get; init; }

        [JsonPropertyName("hpLimiterActive")]
        public bool HpLimiterActive { get; init; }

        [JsonPropertyName("gridXlr1")]
        public float GridXlr1 { get; init; }

        [JsonPropertyName("gridXlr2")]
        public float GridXlr2 { get; init; }

        [JsonPropertyName("gridOut3")]
        public float GridOut3 { get; init; }

        [JsonPropertyName("gridOut4")]
        public float GridOut4 { get; init; }

        [JsonPropertyName("gridSend1")]
        public float GridSend1 { get; init; }

        [JsonPropertyName("gridSend2")]
        public float GridSend2 { get; init; }
    }

    /// <summary>
    /// The desktop app's "CPU Monitor" feature: per-block CPU load, indexed
    /// [row][column] to match the grid. Populated from CPULoad (type 26)
    /// pushes. UNCONFIRMED whether a plain Read query is enough to start this
    /// streaming - see MessageTypes.CPULoad and ProtocolService.RequestCpuLoadRefresh.
    /// </summary>
    public record CpuLoadState
    {
        [JsonPropertyName("totalLoad")]
        public float TotalLoad { get; init; }

        [JsonPropertyName("chains")]
        public List<List<float>> Chains { get; init; } = [];
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

        [JsonPropertyName("ioMeter")]
        public IoMeterState IoMeter { get; init; } = new();

        [JsonPropertyName("cpuLoad")]
        public CpuLoadState CpuLoad { get; init; } = new();

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
        public const uint GridMove = 12;
        public const uint File = 4;
        public const uint IOMeter = 5;
        public const uint Tuner = 6;

        /// <summary>
        /// The desktop app's manual-documented "CPU Monitor" toggle ("displays
        /// overall CPU usage per device block") lives here. Unlike IOMeter, this
        /// has never been sent by this codebase either - added alongside IOMeter
        /// specifically to test whether a plain Read unlocks per-block telemetry
        /// here where it didn't for IOMeter, since CPULoadMessage has no
        /// enable/gate field of its own (just the same action field every other
        /// subsystem uses) and CPU load is comparatively cheap for the device to
        /// compute continuously regardless of what's tapping it.
        /// </summary>
        public const uint CPULoad = 26;

        /// <summary>
        /// Confirmed real and used by the official app via a `strings` pass over
        /// its binary: `neural::cortex::common::GridModelMeterMessageSender`
        /// exists, and a demangled lambda name reveals
        /// `GridModelMeterMessageSender::getMeterMessageBuilder(int, int)` -
        /// almost certainly (row, column), matching this message's only two
        /// meaningful fields. UNCONFIRMED what action/response shape actually
        /// subscribes a cell's live meter - see BuildGridModelMeterSubscribeMessage.
        /// </summary>
        public const uint GridModelMeter = 37;

        /// <summary>
        /// CONFIRMED DISRUPTIVE on hardware: sending ProductionAutomationModeMessage
        /// {enable=true} on this type drops the WebHID connection outright. Tried
        /// it as a hoped-for "enable telemetry" gate for IOMeter/GridModelMeter -
        /// it is not a harmless toggle.
        ///
        /// CONFIRMED IRRELEVANT to that goal, separately: a `strings` pass over
        /// the real Cortex Control app binary (v4.1.0, both the x86_64 and arm64
        /// slices) shows every one of its ~55 other message types has a
        /// `neural::cortex::common::<Type>MessageSender` and/or
        /// `neural::cortex::usb::<Type>MessageReceiver` class (109 such classes
        /// total, e.g. `IOMeterMessageSender`/`IOMeterMessageReceiver` and
        /// `GridModelMeterMessageSender` both genuinely exist) - but NEITHER
        /// class exists for ProductionAutomationMode. The official app never
        /// sends or receives this message at all. Whatever it is, it is not
        /// part of the normal metering/telemetry path and there is no reason
        /// left to keep testing it for that purpose - do not send it again.
        /// </summary>
        public const uint ProductionAutomationMode = 11;
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
