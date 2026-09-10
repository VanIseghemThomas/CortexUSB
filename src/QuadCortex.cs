using OpenCortex.CortexUSB.Client;
using OpenCortex.CortexUSB.Models;
using OpenCortex.CortexUSB.Protocol;
using CortexProtobufV2;

namespace OpenCortex.CortexUSB
{
    /// <summary>
    /// High-level abstraction over the Quad Cortex USB protocol.
    /// Wraps ProtocolService + ProtocolClient and exposes only clean domain operations.
    /// No protobuf, wire, or HID types leak out.
    /// </summary>
    public class QuadCortex : IDisposable
    {
        private readonly ProtocolClient _client;
        private readonly ProtocolService _service;
        private bool _disposed;

        public event Action<StateUpdate>? OnStateChanged
        {
            add => _service.OnStateChanged += value;
            remove => _service.OnStateChanged -= value;
        }

        /// <summary>Raised when the USB connection to the device is lost or recovered.</summary>
        public event Action<bool, string>? OnConnectionStatusChanged
        {
            add => _service.OnConnectionStatusChanged += value;
            remove => _service.OnConnectionStatusChanged -= value;
        }

        public QuadCortex(ProtocolClient? client = null)
        {
            _client = client ?? new ProtocolClient(new UsbHidTransport());
            _service = new ProtocolService(_client);
        }

        public bool IsConnected => _service.IsConnected;

        public async Task<bool> ConnectAsync(TimeSpan? timeout = null)
        {
            // ConfigureAwait(false): see ProtocolService.ConnectAsync's comment on
            // its own dedicated-thread await — this call is entered from the main
            // thread too (via Interop.Connect) and awaits that same not-yet-complete
            // task, so it needs the same fix to avoid the dead main-thread marshal.
            return await _service.ConnectAsync(timeout).ConfigureAwait(false);
        }

        public DeviceStateSummary GetStateSummary() => _service.GetStateSummary();

        public Dictionary<int, ModelInfo> GetModelMap() => _service.GetModelMap();

        // ─── State mutations ──────────────────────────────────────────

        public Task<bool> ChangePresetAsync(string setlistPath, int presetIndex, bool isFactory)
            => _service.ChangePresetAsync(setlistPath, presetIndex, isFactory);

        public Task<bool> SetSceneAsync(int sceneIndex)
            => _service.SetSceneAsync(sceneIndex);

        public Task<bool> SetSceneLabelAsync(int index, string label)
            => _service.SetSceneLabelAsync(index, label);

        public Task<bool> CopySceneAsync(int fromIndex, int toIndex)
            => _service.CopySceneAsync(fromIndex, toIndex);

        public Task<bool> SwapScenesAsync(int indexA, int indexB)
            => _service.SwapScenesAsync(indexA, indexB);

        public Task<bool> SetSceneColorAsync(int index, uint color)
            => _service.SetSceneColorAsync(index, color);

        public Task<bool> SetModeAsync(int mode)
            => _service.SetModeAsync(mode);

        public Task<bool> SetTempoAsync(int bpm)
            => _service.SetTempoAsync(bpm);

        public Task<bool> SetBlockBypassAsync(int row, int col, bool bypassed)
            => _service.SetBlockBypassAsync(row, col, bypassed);

        public Task<bool> SetBlockParameterAsync(int row, int col, int paramIndex, float value)
            => _service.SetBlockParameterAsync(row, col, paramIndex, value);

        public Task<bool> SetBlockAsync(int row, int col, uint modelHash)
            => _service.SetBlockAsync(row, col, modelHash);

        public Task<bool> RemoveBlockAsync(int row, int col)
            => _service.RemoveBlockAsync(row, col);

        public Task<bool> SetChainInputAsync(int row, uint inPortId)
            => _service.SetChainInputAsync(row, inPortId);

        public Task<bool> SetChainOutputAsync(int row, uint outPortId)
            => _service.SetChainOutputAsync(row, outPortId);

        public Task<bool> SetSplitAsync(int row, int splitColumn, int mixColumn)
            => _service.SetSplitAsync(row, splitColumn, mixColumn);

        /// <summary>Assigns a grid cell to a Stomp-mode footswitch (0-7, A-H). A footswitch may drive several cells.</summary>
        public Task<bool> SetStompAssignmentAsync(int row, int col, int footswitchIndex)
            => _service.SetStompAssignmentAsync(row, col, footswitchIndex);

        /// <summary>Unassigns a grid cell from whichever footswitch currently drives it.</summary>
        public Task<bool> ClearStompAssignmentAsync(int row, int col)
            => _service.ClearStompAssignmentAsync(row, col);

        /// <summary>
        /// Sets a footswitch's Latching/Momentary behavior. Silently refused by the device
        /// (no error, no echo) if the footswitch drives more than one cell — check
        /// <see cref="ListStompAssignments"/> first if that matters.
        /// </summary>
        public Task<bool> SetStompMomentaryAsync(int footswitchIndex, bool momentary)
            => _service.SetStompMomentaryAsync(footswitchIndex, momentary);

        /// <summary>Labels a footswitch. Pass single=true when it drives exactly one block.</summary>
        public Task<bool> SetStompLabelAsync(int footswitchIndex, string label, bool single = false)
            => _service.SetStompLabelAsync(footswitchIndex, label, single);

        /// <summary>Which grid cells drive which Stomp-mode footswitch, for the current preset.</summary>
        public IReadOnlyList<StompAssignment> ListStompAssignments()
            => _service.CurrentState.PresetDetails?.StompAssignments ?? [];

        /// <summary>Per-footswitch label/momentary state for the current preset, sparse (A-H, index 0-7).</summary>
        public IReadOnlyList<FootswitchInfo> ListFootswitches()
            => _service.CurrentState.PresetDetails?.Footswitches ?? [];

        public Task<bool> SavePresetAsync(string setlistPath, string slot, string name, int instrument = 0)
            => _service.SavePresetAsync(setlistPath, slot, name, instrument);

        public Task<bool> DeletePresetAsync(string setlistPath, string presetName)
            => _service.DeletePresetAsync(setlistPath, presetName);

        public Task<bool> MovePresetAsync(string setlistPath, string presetName, string toSlot)
            => _service.MovePresetAsync(setlistPath, presetName, toSlot);

        /// <summary>Renames a preset in place. UNCONFIRMED against hardware — see ProtobufBuilder.BuildRenamePresetMessage.</summary>
        public Task<bool> RenamePresetAsync(string setlistPath, string oldName, string newName)
            => _service.RenamePresetAsync(setlistPath, oldName, newName);

        /// <summary>Creates a new user setlist. Returns its device path, or null on failure.</summary>
        public Task<string?> CreateSetlistAsync(string name)
            => _service.CreateSetlistAsync(name);

        public Task<bool> DeleteSetlistAsync(string name)
            => _service.DeleteSetlistAsync(name);

        /// <summary>
        /// Copies a preset into another setlist (or a different slot in the same
        /// one). Not a device-level operation — the unit has no host-drivable
        /// copy — this recalls the source preset then saves the grid into the
        /// destination slot, matching what the unit's own copy/paste turns out to
        /// do. This CHANGES what is loaded on the unit and leaves the source
        /// preset on the grid afterwards; it copies the preset's audio state, not
        /// its metadata (tags aren't carried over — see <see cref="SavePresetAsync"/>).
        /// <paramref name="toSlot"/> is a linear index (0-255) or a slot name like "28D".
        /// </summary>
        public async Task<bool> CopyPresetAsync(string fromSetlistPath, int fromIndex, bool fromIsFactory,
            string toSetlistPath, string toSlot, string name, int instrument = 0)
        {
            bool recalled = await _service.ChangePresetAsync(fromSetlistPath, fromIndex, fromIsFactory);
            if (!recalled) return false;

            return await _service.SavePresetAsync(toSetlistPath, toSlot, name, instrument);
        }

        public Task<bool> SetGlobalEqBandAsync(int band, float? gain = null, float? frequency = null,
            float? q = null, float? filterType = null, bool? enabled = null)
            => _service.SetGlobalEqBandAsync(band, gain, frequency, q, filterType, enabled);

        public Task<bool> SetGlobalEqOutputAsync(float? level = null, bool? out12 = null, bool? out34 = null)
            => _service.SetGlobalEqOutputAsync(level, out12, out34);

        public Task<bool> SetGlobalEqBypassAsync(bool bypassed)
            => _service.SetGlobalEqBypassAsync(bypassed);

        public Task<bool> SetMasterVolumeAsync(float volume)
            => _service.SetMasterVolumeAsync(volume);

        public Task<bool> SetTunerInputAsync(int inputPortId)
            => _service.SetTunerInputAsync(inputPortId);

        public Task<bool> SetTunerMuteAsync(bool mute)
            => _service.SetTunerMuteAsync(mute);

        public Task<bool> RestoreAudioAsync()
            => _service.RestoreAudioAsync();

        public bool RequestGlobalControlsRefresh()
            => _service.RequestGlobalControlsRefresh();

        // ─── Preset library (cached, with device fallback) ─────────────

        public IReadOnlyList<FlatPreset> ListPresets()
        {
            List<PresetDirectory> library = _service.CurrentState.PresetLibrary;
            if (library.Count > 0)
            {
                List<FlatPreset> flat = [];
                FlattenPresets(library, flat);
                if (flat.Count > 0)
                {
                    return flat;
                }
            }

            if (_client.IsConnected)
            {
                IList<ProductData> presets = _client.GetLoadedPresets(TimeSpan.FromSeconds(5));
                return presets.Select(p => new FlatPreset { Name = p.Name ?? "", Index = p.Index }).ToList();
            }

            return [];
        }

        /// <summary>
        /// Lists user setlist names under <see cref="ProtobufBuilder.UserSetlistRoot"/>,
        /// including EMPTY ones. Unlike <see cref="ListPresets"/> (which derives a
        /// setlist's existence purely from having at least one preset in it — the
        /// FlatPreset[] shape has no way to represent an empty folder), this reads
        /// the raw directory tree directly, so a setlist created via
        /// <see cref="CreateSetlistAsync"/> shows up here even before anything is
        /// saved into it.
        /// </summary>
        public IReadOnlyList<string> ListSetlists()
        {
            List<PresetDirectory> library = _service.CurrentState.PresetLibrary;
            PresetDirectory? presetsRoot = library.FirstOrDefault(d => d.Path == ProtobufBuilder.UserSetlistRoot);
            return presetsRoot?.Children.Select(c => c.Name).ToList() ?? [];
        }

        public IReadOnlyList<FlatPreset> ListPlugins()
        {
            List<PresetDirectory> library = _service.CurrentState.PresetLibrary;
            List<FlatPreset> plugins = [];

            foreach (PresetDirectory dir in library)
            {
                if (dir.Path.StartsWith("/opt/neuraldsp/Plugins", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (PresetEntry entry in dir.Presets)
                    {
                        plugins.Add(new FlatPreset { Name = entry.Name, Index = entry.Index, Path = entry.Path, Author = entry.Author, SetlistPath = dir.Path });
                    }
                }
                plugins.AddRange(FindPluginsRecursive(dir.Children));
            }

            return plugins;
        }

        // ─── Preset loading (cache → device fallback all inside) ───────

        public async Task<bool> LoadPresetAsync(string presetPath)
        {
            PresetLocation loc = FindPresetByPath(presetPath);

            if (loc.FolderKey == null || loc.PresetIndex < 0)
            {
                loc = await Task.Run(() =>
                    _client.FindPresetByPathFromDevice(presetPath, TimeSpan.FromSeconds(5)));
            }

            if (loc.FolderKey != null && loc.PresetIndex >= 0)
            {
                return await _service.ChangePresetAsync(loc.FolderKey, loc.PresetIndex, loc.IsFactory);
            }

            return false;
        }

        public async Task<bool> SwitchPresetAsync(string presetName)
        {
            PresetLocation loc = FindPresetInLibrary(presetName);

            if (loc.FolderKey != null && loc.PresetIndex >= 0)
            {
                byte[] message = ProtobufBuilder.BuildSetlistPositionMessage(loc.FolderKey, loc.PresetIndex, loc.IsFactory);
                _client.SendWireMessage(message, 2);
                WirePayload? resp = await Task.Run(() =>
                    _client.WaitForMessage(15, TimeSpan.FromSeconds(5)));
                return resp != null;
            }

            return await Task.Run(() =>
                _client.RecallPresetByName(presetName, TimeSpan.FromSeconds(5)));
        }

        // ─── Library search helpers ────────────────────────────────────

        private PresetLocation FindPresetByPath(string path)
        {
            List<PresetDirectory> library = _service.CurrentState.PresetLibrary;
            return FindPresetByPathRecursive(library, path);
        }

        private static PresetLocation FindPresetByPathRecursive(
            List<PresetDirectory> dirs, string path)
        {
            foreach (PresetDirectory dir in dirs)
            {
                PresetEntry? match = dir.Presets.FirstOrDefault(e =>
                    string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return new PresetLocation(dir.Path ?? string.Empty, match.Index, false);
                }
                PresetLocation result = FindPresetByPathRecursive(dir.Children, path);
                if (result.FolderKey != null) return result;
            }
            return new PresetLocation(null, -1, false);
        }

        private PresetLocation FindPresetInLibrary(string name)
        {
            List<PresetDirectory> library = _service.CurrentState.PresetLibrary;
            return FindPresetRecursive(library, name);
        }

        private static PresetLocation FindPresetRecursive(
            List<PresetDirectory> dirs, string name)
        {
            foreach (PresetDirectory dir in dirs)
            {
                PresetEntry? match = dir.Presets.FirstOrDefault(e =>
                    string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return new PresetLocation(dir.Path ?? string.Empty, match.Index, false);
                }
                PresetLocation result = FindPresetRecursive(dir.Children, name);
                if (result.FolderKey != null) return result;
            }
            return new PresetLocation(null, -1, false);
        }

        private static void FlattenPresets(List<PresetDirectory> dirs, List<FlatPreset> result)
        {
            foreach (PresetDirectory dir in dirs)
            {
                foreach (PresetEntry entry in dir.Presets)
                {
                    result.Add(new FlatPreset { Name = entry.Name, Index = entry.Index, Path = entry.Path, Author = entry.Author, SetlistPath = dir.Path });
                }
                FlattenPresets(dir.Children, result);
            }
        }

        private static List<FlatPreset> FindPluginsRecursive(List<PresetDirectory> dirs)
        {
            List<FlatPreset> result = [];
            foreach (PresetDirectory dir in dirs)
            {
                if (dir.Path.StartsWith("/opt/neuraldsp/Plugins", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (PresetEntry entry in dir.Presets)
                    {
                        result.Add(new FlatPreset { Name = entry.Name, Index = entry.Index, Path = entry.Path, Author = entry.Author, SetlistPath = dir.Path });
                    }
                }
                result.AddRange(FindPluginsRecursive(dir.Children));
            }
            return result;
        }

        // ─── Dispose ───────────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                _service.Dispose();
                _client.Dispose();
            }
        }
    }
}
