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
            return await _service.ConnectAsync(timeout);
        }

        public DeviceStateSummary GetStateSummary() => _service.GetStateSummary();

        public Dictionary<int, ModelInfo> GetModelMap() => _service.GetModelMap();

        // ─── State mutations ──────────────────────────────────────────

        public Task<bool> ChangePresetAsync(string setlistPath, int presetIndex, bool isFactory)
            => _service.ChangePresetAsync(setlistPath, presetIndex, isFactory);

        public Task<bool> SetSceneAsync(int sceneIndex)
            => _service.SetSceneAsync(sceneIndex);

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

        public Task<bool> SavePresetAsync(string setlistPath, string slot, string name, int instrument = 0)
            => _service.SavePresetAsync(setlistPath, slot, name, instrument);

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
                return flat;
            }

            if (_client.IsConnected)
            {
                IList<ProductData> presets = _client.GetLoadedPresets(TimeSpan.FromSeconds(5));
                return presets.Select(p => new FlatPreset(p.Name ?? "", p.Index, "", "", "")).ToList();
            }

            return [];
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
                        plugins.Add(new FlatPreset(entry.Name, entry.Index, entry.Path, entry.Author, dir.Path));
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
                    result.Add(new FlatPreset(entry.Name, entry.Index, entry.Path, entry.Author, dir.Path));
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
                        result.Add(new FlatPreset(entry.Name, entry.Index, entry.Path, entry.Author, dir.Path));
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
