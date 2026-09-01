using System.Collections.Concurrent;
using OpenCortex.CortexUSB.Client;
using OpenCortex.CortexUSB.Models;
using OpenCortex.CortexUSB.Protocol;
using CortexProtobufV2;
using Microsoft.Extensions.Logging;

namespace OpenCortex.CortexUSB
{
    /// <summary>
    /// High-level service for managing Quad Cortex protocol operations and state.
    /// Provides thread-safe operations and state caching
    /// </summary>
    public class ProtocolService : IDisposable
    {
        private sealed record ParsedFolder(string Path, string Name, List<PresetEntry> Presets);
        private sealed record ChainIO(string Input, string Output, int InPortId, int OutPortId);
        private sealed record NamedRoot(string Path, string Name);
        private sealed record ParamWrite(int Offset, float Value);
        private sealed record ChainBlocksResult(List<Block> Blocks, List<SplitInfo> Splits);

        // Mirrors WebCortex/src/types/PortEnums.ts — in_portid/out_portid are raw device
        // enum values (pyquadcortex protocol.enums.Input/Output), not something we can
        // derive from any other field on the wire.
        private static readonly Dictionary<int, string> InputPortNames = new()
        {
            [0] = "Empty",
            [1] = "Input 1",
            [2] = "Input 2",
            [3] = "Input 1/2",
            [4] = "Return 1",
            [5] = "Return 2",
            [6] = "Return 1/2",
            [7] = "Prev. Row",
            [8] = "USB 5",
            [9] = "USB 6",
            [10] = "USB 7",
            [11] = "USB 8",
            [12] = "USB 5/6",
            [13] = "USB 7/8",
            [14] = "Sidechain",
        };

        private static readonly Dictionary<int, string> OutputPortNames = new()
        {
            [0] = "Empty",
            [1] = "Output 1/2",
            [2] = "Output 3/4",
            [3] = "Send 1/2",
            [4] = "Output 1",
            [5] = "Output 2",
            [6] = "Output 3",
            [7] = "Output 4",
            [8] = "Send 1",
            [9] = "Send 2",
            [10] = "USB 5",
            [11] = "USB 6",
            [12] = "USB 7",
            [13] = "USB 8",
            [14] = "USB 5/6",
            [15] = "USB 7/8",
            [16] = "Next Row 3",
            [17] = "Next Row 4",
            [18] = "Next Row 3/4",
            [19] = "Multiple (Multi-Out)",
            [20] = "USB 3",
            [21] = "USB 4",
            [22] = "USB 3/4",
        };
        private readonly ProtocolClient _client;
        private readonly ILogger<ProtocolService> _logger;
        private readonly ConcurrentQueue<WirePayload> _incomingMessages;
        private readonly CancellationTokenSource _cts;
        private readonly Thread? _messageProcessorThread;
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _operationSemaphore = new(1, 1);

        // State cache
        private DeviceState _currentState;
        private Dictionary<int, ModelInfo> _modelMap = [];
        private List<GridRow> _grid = [];
        private BinaryPreset? _currentPreset;
        private readonly List<byte[]> _fileMessages = [];
        private DateTime _lastLibraryRebuild = DateTime.MinValue;
        private volatile bool _isConnected;

        /// <summary>
        /// Raised when device state changes (scene, mode, BPM, etc.).
        /// </summary>
        public event Action<StateUpdate>? OnStateChanged;

        /// <summary>
        /// Raised when the USB connection to the device is lost or recovered, after the
        /// first successful <see cref="ConnectAsync"/>. `connected` reflects the new
        /// state; `reason` is a short human-readable description for logging/UI.
        /// </summary>
        public event Action<bool, string>? OnConnectionStatusChanged;

        private readonly Timer? _statePollingTimer;
        private bool _suppressStateEvents;

        // ─── Reconnect watchdog ─────────────────────────────────────────
        // Runs for the lifetime of the service (not just while connected) so it can
        // detect and recover from a USB drop without requiring the browser client to
        // reconnect its WebSocket — a stale bridge-side cache was the root cause of
        // the frontend showing a preset the device had long since moved on from.
        private readonly Timer _reconnectWatchdogTimer;
        private volatile bool _everConnected;
        private static readonly TimeSpan ReconnectWatchdogInterval = TimeSpan.FromSeconds(3);

        // Best-effort tempo write confirmation. The device pushes GlobalTempo (33)
        // messages continuously; a push reporting the value we just wrote confirms
        // the write landed. Absence within the wait is ambiguous (tempo MODE may be
        // GLOBAL, in which case the preset value is stored but not audible), so a
        // timeout is logged but not treated as a hard failure.
        private TaskCompletionSource<bool>? _tempoEchoTcs;
        private int _expectedTempoBpm;

        /// <summary>
        /// Refreshes the current state by querying key fields from the device.
        /// This ensures we get the most up-to-date state instead of relying on cached values.
        /// </summary>
        public async Task<bool> RefreshCurrentStateAsync(bool skipLock = false)
        {
            if (!_isConnected)
                return false;

            if (!skipLock)
            {
                await _operationSemaphore.WaitAsync(_cts.Token);
            }
            try
            {
                _logger.LogDebug("[ProtocolService] Forcing fresh preset and scene state query from device...");

                // These four queries are each a blocking wait (3-5s worst case) on
                // its own message type. Run them concurrently instead of one after
                // another — sequential worst case (~16s) exceeded the frontend's
                // request timeout for large/slow-to-settle presets, and since this
                // whole method runs under _operationSemaphore, a single slow refresh
                // blocked every other queued operation behind it too.
                bool[] updated = await Task.WhenAll(
                    Task.Run(QueryAndUpdatePresetPosition),
                    Task.Run(QueryAndUpdateRecallPreset),
                    Task.Run(() => QueryAndUpdateStateField(MessageTypes.Scene, "Scene",
                        (payload) => { int s = ParseSceneMessage(payload); return s >= 0 ? s : (int?)null; },
                        (state, val) => val != state.Scene,
                        (state, val, ts) => state with { Scene = val, Timestamp = ts },
                        (val) => _logger.LogDebug("[ProtocolService] Scene: {Scene}", val))),
                    Task.Run(() => QueryAndUpdateStateField(MessageTypes.Mode, "Mode",
                        (payload) => { int m = ParseModeMessage(payload); return m >= 0 ? m : (int?)null; },
                        (state, val) => val != state.Mode,
                        (state, val, ts) => state with { Mode = val, Timestamp = ts },
                        (val) => _logger.LogDebug("[ProtocolService] Mode: {Mode}", DeviceMode.GetModeName(val))))
                );
                bool anyUpdated = updated.Any(u => u);
                if (_currentPreset != null)
                {
                    lock (_stateLock)
                    {
                        int bpm = ExtractBpmFromPreset(_currentPreset, _currentState.Scene);
                        _grid = BuildGrid(_currentPreset, _currentState.Scene);
                        _currentState = _currentState with
                        {
                            Grid = _grid,
                            Bpm = bpm > 0 ? bpm : _currentState.Bpm,
                            Timestamp = DateTime.UtcNow
                        };
                        if (bpm > 0) _logger.LogDebug("[ProtocolService] BPM from preset: {Bpm}", bpm);
                    }
                }

                _logger.LogDebug("[ProtocolService] Fresh complete state: Preset={PresetName}, Scene={Scene}, Mode={Mode}, BPM={Bpm}",
                    _currentState.PresetDetails?.Name, _currentState.Scene, DeviceMode.GetModeName(_currentState.Mode), _currentState.Bpm);

                if (anyUpdated)
                {
                    FireStateChanged(StateUpdate.FromDevice(_currentState));
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Failed to refresh state");
                return false;
            }
            finally
            {
                if (!skipLock)
                {
                    _operationSemaphore.Release();
                }
            }
        }

        private bool QueryAndUpdatePresetPosition()
        {
            _logger.LogDebug("[ProtocolService] Querying current preset position...");
            byte[] query = ProtobufBuilder.BuildStateQuery();
            if (!SendCommand(query, MessageTypes.SetlistPosition)) return false;

            WirePayload? response = _client.WaitForMessage(MessageTypes.SetlistPosition, TimeSpan.FromSeconds(5));
            if (response == null) return false;

            PresetInfo? presetInfo = ParseSetlistPosition(response.Payload);
            if (presetInfo == null) return false;

            lock (_stateLock)
            {
                if (_currentState.CurrentPreset?.PresetIndex == presetInfo.PresetIndex &&
                    _currentState.CurrentPreset?.SetlistPath == presetInfo.SetlistPath)
                    return false;

                _currentState = _currentState with { CurrentPreset = presetInfo, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] Preset position: {SetlistPath}[{PresetIndex}]", presetInfo.SetlistPath, presetInfo.PresetIndex);
                return true;
            }
        }

        private bool QueryAndUpdateRecallPreset()
        {
            _logger.LogDebug("[ProtocolService] Querying preset details...");
            byte[] query = ProtobufBuilder.BuildStateQuery();
            if (!SendCommand(query, MessageTypes.RecallPreset)) return false;

            WirePayload? response = _client.WaitForMessage(MessageTypes.RecallPreset, TimeSpan.FromSeconds(5));
            if (response == null) return false;

            BinaryPreset? preset = ParseRecallPreset(response.Payload);
            if (preset == null) return false;

            lock (_stateLock)
            {
                _currentPreset = preset;
                PresetDetails? details = BuildPresetDetails(preset);

                int bpm = ExtractBpmFromPreset(preset, _currentState.Scene);

                if (_currentState.PresetDetails?.Name == details?.Name &&
                    _currentState.PresetDetails?.Uuid == details?.Uuid &&
                    bpm == _currentState.Bpm)
                    return false;

                _currentState = _currentState with
                {
                    PresetDetails = details,
                    Bpm = bpm > 0 ? bpm : _currentState.Bpm,
                    Timestamp = DateTime.UtcNow
                };
                _logger.LogDebug("[ProtocolService] Preset details: '{Name}' by {Author}, BPM={Bpm}", details?.Name, details?.Author, bpm);
                return true;
            }
        }

        private bool QueryAndUpdateStateField<T>(
            uint messageType, string fieldName,
            Func<byte[], T?> parseValue,
            Func<DeviceState, T, bool> hasChanged,
            Func<DeviceState, T, DateTime, DeviceState> updateState,
            Action<T>? onUpdated = null) where T : struct
        {
            _logger.LogDebug("[ProtocolService] Querying {FieldName}...", fieldName);
            byte[] query = ProtobufBuilder.BuildStateQuery();
            if (!SendCommand(query, messageType)) return false;

            WirePayload? response = _client.WaitForMessage(messageType, TimeSpan.FromSeconds(3));
            if (response == null) return false;

            T? value = parseValue(response.Payload);
            if (value == null) return false;

            lock (_stateLock)
            {
                if (!hasChanged(_currentState, value.Value))
                    return false;

                _currentState = updateState(_currentState, value.Value, DateTime.UtcNow);
                onUpdated?.Invoke(value.Value);
                return true;
            }
        }

        /// <summary>
        /// Gets the current device state (thread-safe).
        /// </summary>
        public DeviceState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentState;
                }
            }
        }

        /// <summary>
        /// Gets a lightweight state summary without heavy collections (ModelMap, PresetLibrary).
        /// Use for API responses to avoid sending large payloads.
        /// </summary>
        public DeviceStateSummary GetStateSummary()
        {
            lock (_stateLock)
            {
                return new DeviceStateSummary(
                    _currentState.CurrentPreset,
                    _currentState.PresetDetails,
                    _currentState.Scene,
                    _currentState.Mode,
                    _currentState.Bpm,
                    _currentState.Grid,
                    _currentState.Timestamp,
                    _currentState.GlobalEq,
                    _currentState.MasterVolume,
                    _currentState.Tuner
                );
            }
        }

        /// <summary>
        /// Gets the model map (model ID -> ModelInfo) for external queries.
        /// </summary>
        public Dictionary<int, ModelInfo> GetModelMap()
        {
            lock (_stateLock)
            {
                return new Dictionary<int, ModelInfo>(_modelMap);
            }
        }

        /// <summary>
        /// Finds a preset in the cached library by its full file path.
        /// Returns a PresetLocation for use with ChangePresetAsync.
        /// </summary>
        public PresetLocation FindPresetByPath(string presetPath)
        {
            lock (_stateLock)
            {
                return FindPresetByPathRecursive(_currentState.PresetLibrary, presetPath);
            }
        }

        private static PresetLocation FindPresetByPathRecursive(
            List<PresetDirectory> dirs, string presetPath)
        {
            foreach (PresetDirectory dir in dirs)
            {
                PresetEntry? match = dir.Presets.FirstOrDefault(e =>
                    string.Equals(e.Path, presetPath, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return new PresetLocation(dir.Path ?? string.Empty, match.Index, false);
                }
                PresetLocation result = FindPresetByPathRecursive(dir.Children, presetPath);
                if (result.FolderKey != null) return result;
            }
            return new PresetLocation(null, -1, false);
        }

        /// <summary>
        /// Gets whether the device is currently connected.
        /// </summary>
        public bool IsConnected => _isConnected;

        /// Wraps ProtocolClient.SendWireMessage, returning false on failure.
        /// Replaces the old IProtocolHandler.SendMessage(bool) pattern.
        private bool SendCommand(byte[] payload, uint messageType)
        {
            try
            {
                _client.SendWireMessage(payload, messageType);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public ProtocolService(ProtocolClient? client = null, ILogger<ProtocolService>? logger = null)
        {
            _logger = logger ?? new SimpleConsoleLogger<ProtocolService>();
            _client = client ?? new ProtocolClient(new UsbHidTransport());
            _incomingMessages = new ConcurrentQueue<WirePayload>();
            _cts = new CancellationTokenSource();
            _currentState = new DeviceState();

            // Bridge ProtocolClient messages into the ProtocolService processing pipeline
            _client.OnMessageReceived += msg => _incomingMessages.Enqueue(msg);
            _client.ConnectionLost += HandleClientConnectionLost;

            // Start background message processor
            _messageProcessorThread = new Thread(MessageProcessorLoop)
            {
                Name = "ProtocolServiceProcessor",
                IsBackground = true
            };
            _messageProcessorThread.Start();

            // Initialize polling timer (but don't start it until connected)
            _statePollingTimer = new Timer(PollHardwareState, null, Timeout.Infinite, Timeout.Infinite);

            // The reconnect watchdog runs for the service's whole lifetime; it no-ops
            // until _everConnected is set (see ConnectAsync) and while already connected.
            _reconnectWatchdogTimer = new Timer(ReconnectWatchdogTick, null, ReconnectWatchdogInterval, ReconnectWatchdogInterval);
        }

        /// <summary>
        /// Invoked (on the ProtocolClient reader thread) when a USB stall is detected.
        /// The transport has already been torn down by the time this fires.
        /// </summary>
        private void HandleClientConnectionLost(string reason)
        {
            if (!_isConnected) return; // already known-disconnected; avoid duplicate events
            _isConnected = false;
            _logger.LogWarning("[ProtocolService] Device connection lost: {Reason}", reason);
            OnConnectionStatusChanged?.Invoke(false, reason);
        }

        /// <summary>
        /// Periodically attempts to re-establish the USB connection after a detected
        /// drop, and refreshes the cached state on success so stale preset/grid data
        /// (from before the drop) never lingers past a reconnect.
        /// </summary>
        private async void ReconnectWatchdogTick(object? state)
        {
            if (_disposed || _isConnected || !_everConnected) return;

            // Never block: if a normal operation currently holds the semaphore, just
            // skip this tick and retry on the next one rather than queuing behind it.
            if (!await _operationSemaphore.WaitAsync(0))
                return;

            try
            {
                if (_isConnected) return; // re-check now that we hold the lock

                _logger.LogInformation("[ProtocolService] Attempting to reconnect to device...");
                bool reconnected = await Task.Run(() => _client.Connect(TimeSpan.FromSeconds(3)));
                if (!reconnected) return;

                _isConnected = true;
                _logger.LogInformation("[ProtocolService] Reconnected — refreshing state...");

                _suppressStateEvents = true;
                await QueryInitialStateAsync();
                _suppressStateEvents = false;

                OnConnectionStatusChanged?.Invoke(true, "Device reconnected");
                FireStateChanged(StateUpdate.FromDevice(_currentState));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Reconnect attempt failed");
            }
            finally
            {
                _suppressStateEvents = false;
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Connects to the device and performs handshake.
        /// </summary>
        public async Task<bool> ConnectAsync(TimeSpan? timeout = null)
        {
            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                if (_isConnected)
                    return true;

                TimeSpan actualTimeout = timeout ?? TimeSpan.FromSeconds(10);
                _logger.LogInformation("[ProtocolService] Connecting with timeout {TimeoutSeconds}s...", actualTimeout.TotalSeconds);

                // Suppress state events during initialization to avoid spam
                _suppressStateEvents = true;

                // Perform handshake on background thread
                bool connected = await Task.Run(() => _client.Connect(actualTimeout), _cts.Token);

                if (!connected)
                {
                    _logger.LogWarning("[ProtocolService] Connection failed");
                    _suppressStateEvents = false;
                    return false;
                }

                _isConnected = true;
                _everConnected = true;
                _logger.LogInformation("[ProtocolService] Connected");

                // Query initial state
                await QueryInitialStateAsync();

                // Re-enable state events and fire a single complete event
                _suppressStateEvents = false;
                _logger.LogDebug("[ProtocolService] State event suppression disabled, ready for hardware changes");

                // Disable background polling - use on-demand refresh instead
                // _statePollingTimer?.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
                _logger.LogDebug("[ProtocolService] Using on-demand state refresh instead of background polling");

                // Notify state change - single event after full initialization
                FireStateChanged(StateUpdate.FromDevice(_currentState));

                return true;
            }
            finally
            {
                _suppressStateEvents = false;
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Queries the device for initial runtime state (scene, mode, tempo, preset).
        /// ModelRepo is already fetched during the handshake (before KeepAlive), so it
        /// is NOT re-requested here.  The ModelRepo response received during Connect()
        /// will arrive in the message queue and be processed by the background thread.
        /// </summary>
        private async Task QueryInitialStateAsync()
        {
            _logger.LogDebug("[ProtocolService] Querying initial state...");

            // Query Setlist Position (type 2) — which preset slot is loaded
            await QueryStateFieldAsync(MessageTypes.SetlistPosition, "SetlistPosition", TimeSpan.FromSeconds(5));

            // Query RecallPreset (type 15) — full preset data including scene labels
            await QueryStateFieldAsync(MessageTypes.RecallPreset, "RecallPreset", TimeSpan.FromSeconds(5));

            // Query Scene (type 13)
            await QueryStateFieldAsync(MessageTypes.Scene, "Scene");

            // Query Mode (type 14)
            await QueryStateFieldAsync(MessageTypes.Mode, "Mode");

            // Query Tempo (type 33)
            await QueryStateFieldAsync(MessageTypes.Tempo, "Tempo");

            // Request preset library listing (type 4)
            await RequestPresetLibraryAsync(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(1), true);

            _logger.LogDebug("[ProtocolService] Initial state: Scene={Scene}, Mode={Mode}, BPM={Bpm}",
                _currentState.Scene, DeviceMode.GetModeName(_currentState.Mode), _currentState.Bpm);
        }

        private async Task QueryStateFieldAsync(uint messageType, string fieldName, TimeSpan? responseTimeout = null)
        {
            try
            {
                byte[] query = ProtobufBuilder.BuildStateQuery();
                if (SendCommand(query, messageType))
                {
                    // Wait for response (short timeout - device responds quickly)
                    await Task.Run(() =>
                    {
                        WirePayload? response = _client.WaitForMessage(messageType, responseTimeout ?? TimeSpan.FromSeconds(2));
                        if (response != null)
                        {
                            _incomingMessages.Enqueue(response);
                            _logger.LogDebug("[ProtocolService] Queried {FieldName}: received {ByteCount} bytes", fieldName, response.Payload.Length);
                        }
                        else
                        {
                            _logger.LogWarning("[ProtocolService] No response for {FieldName} query", fieldName);
                        }
                    }, _cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error querying {FieldName}", fieldName);
            }
        }

        /// <summary>
        /// Sets the current scene (0-7).
        /// </summary>
        /// <summary>
        /// Changes the current preset.
        /// </summary>
        public async Task<bool> ChangePresetAsync(string setlistPath, int presetIndex, bool isFactory)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot change preset - not connected");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogInformation("[ProtocolService] Changing to preset {PresetIndex} in '{SetlistPath}' (factory={IsFactory})...", presetIndex, setlistPath, isFactory);

                // Suppress intermediate state events — the device pushes responses
                // during the delay and queries below, each of which would fire a
                // separate stateUpdate. We fire exactly one consolidated event at the end.
                _suppressStateEvents = true;

                byte[] message = ProtobufBuilder.BuildSetlistPositionMessage(setlistPath, presetIndex, isFactory);
                if (!SendCommand(message, MessageTypes.SetlistPosition))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send preset change message");
                    return false;
                }

                _logger.LogDebug("[ProtocolService] Preset change command sent");

                // Wait a bit for the device to switch presets
                await Task.Delay(500);

                // Refresh state to get the new preset data (skipLock: true — caller holds _operationSemaphore)
                await RefreshCurrentStateAsync(skipLock: true);

                _suppressStateEvents = false;

                // Fire exactly one state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "preset"));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error changing preset");
                return false;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> SetSceneAsync(int sceneIndex)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot set scene - not connected");
                return false;
            }

            if (sceneIndex < 0 || sceneIndex > 7)
            {
                _logger.LogWarning("[ProtocolService] Invalid scene {SceneIndex} (must be 0-7)", sceneIndex);
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                // No-op if the device already reports this scene.
                int currentScene;
                lock (_stateLock) { currentScene = _currentState.Scene; }
                if (currentScene == sceneIndex)
                {
                    _logger.LogDebug("[ProtocolService] Scene already {SceneIndex}, skipping write", sceneIndex);
                    return true;
                }

                _logger.LogDebug("[ProtocolService] Setting scene to {SceneIndex}...", sceneIndex);

                byte[] message = ProtobufBuilder.BuildSceneMessage(sceneIndex);
                if (!SendCommand(message, MessageTypes.Scene))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send scene message");
                    return false;
                }

                // Verify: the device pushes a Scene message confirming the switch.
                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.Scene,
                    p => ParseSceneMessage(p.Payload) == sceneIndex,
                    TimeSpan.FromSeconds(2));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Scene {SceneIndex} sent but no device echo within 2s", sceneIndex);
                    return false;
                }

                // Update state optimistically (dispatch loop may have already done this)
                lock (_stateLock)
                {
                    _currentState = _currentState with { Scene = sceneIndex, Timestamp = DateTime.UtcNow };
                }

                _logger.LogInformation("[ProtocolService] Scene set to {SceneIndex} (device echo confirmed)", sceneIndex);

                // Fire state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "scene"));

                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the device mode (0=Preset, 1=Scene, 2=Stomp).
        /// </summary>
        public async Task<bool> SetModeAsync(int mode)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot set mode - not connected");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting mode to {Mode}...", DeviceMode.GetModeName(mode));

                byte[] message = ProtobufBuilder.BuildModeMessage(mode);
                if (!SendCommand(message, MessageTypes.Mode))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send mode message");
                    return false;
                }

                // Verify: the device pushes a Mode message confirming the change.
                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.Mode,
                    p => ParseModeMessage(p.Payload) == mode,
                    TimeSpan.FromSeconds(2));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Mode {Mode} sent but no device echo within 2s", DeviceMode.GetModeName(mode));
                    return false;
                }

                // Update state optimistically
                lock (_stateLock)
                {
                    _currentState = _currentState with { Mode = mode, Timestamp = DateTime.UtcNow };
                }

                _logger.LogInformation("[ProtocolService] Mode set to {Mode} (device echo confirmed)", DeviceMode.GetModeName(mode));

                // Fire state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "mode"));

                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the tempo/BPM (40-240).
        /// </summary>
        public async Task<bool> SetTempoAsync(int bpm)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot set tempo - not connected");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting BPM to {Bpm}...", bpm);

                // Per-preset tempo is a Grid UPDATE on tempoProgramData (model 25000),
                // not a GlobalTempo message. Sending type 33 was the old bug: it
                // "succeeded" while the hardware BPM never changed.
                byte[] message = ProtobufBuilder.BuildTempoGridMessage(bpm);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send tempo message");
                    return false;
                }

                // Arm the echo listener before committing to the optimistic update.
                TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _tempoEchoTcs = tcs;
                _expectedTempoBpm = bpm;

                // Update state optimistically
                lock (_stateLock)
                {
                    _currentState = _currentState with { Bpm = bpm, Timestamp = DateTime.UtcNow };
                }

                // Wait briefly for the device's own GlobalTempo push to echo the
                // value back. Best-effort: a timeout is logged, not a hard failure,
                // because a GLOBAL tempo MODE legitimately means the preset value we
                // stored is not the one the unit reports as playing.
                Task echoTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                bool echoed = ReferenceEquals(echoTask, tcs.Task) && tcs.Task.IsCompletedSuccessfully;

                if (echoed)
                {
                    _logger.LogInformation("[ProtocolService] BPM set to {Bpm} (device echo confirmed)", bpm);
                }
                else
                {
                    _logger.LogWarning("[ProtocolService] BPM {Bpm} sent but no device echo within 2s — tempo MODE may be GLOBAL, so the preset value may be stored but not audible until PRESET mode", bpm);
                }

                // Fire state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "tempo"));

                return true;
            }
            finally
            {
                _tempoEchoTcs = null;
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the bypass state for a block in the current scene.
        /// </summary>
        public async Task<bool> SetBlockBypassAsync(int rowIndex, int columnIndex, bool bypassed)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot set block bypass - not connected");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting block bypass: row={Row}, col={Col}, bypassed={Bypassed}", rowIndex, columnIndex, bypassed);

                // Use simplified bypass message (no scene parameter needed)
                byte[] message = ProtobufBuilder.BuildGridBypassMessage(rowIndex, columnIndex, bypassed);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send grid bypass message");
                    return false;
                }

                // Verify: grid edits always produce a Grid broadcast. A precise match
                // (bypass[row][col] at the requested value) is the strongest signal;
                // any Grid echo at all still means the edit was accepted — DSP-refused
                // writes produce no echo. Treat a total absence as a failure.
                if (!WaitForGridEcho(p => GridEchoConfirmsBypass(p, rowIndex, columnIndex, bypassed),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but bypass [{Row},{Col}]={Bypassed} not matched — write may not have taken", rowIndex, columnIndex, bypassed);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for bypass [{Row},{Col}] — write likely refused (e.g. invalid position)", rowIndex, columnIndex);
                    return false;
                }
                if (preciseMatch)
                {
                    _logger.LogDebug("[ProtocolService] Bypass [{Row},{Col}]={Bypassed} confirmed by device echo", rowIndex, columnIndex, bypassed);
                }

                // Update state optimistically - update the grid
                lock (_stateLock)
                {
                    if (_grid.Count > rowIndex && _grid[rowIndex].Blocks.Count > columnIndex)
                    {
                        GridRow row = _grid[rowIndex];
                        List<Block> updatedBlocks = row.Blocks.Select((block, index) =>
                        {
                            // Match by position in array, not SlotIndex
                            if (index == columnIndex)
                            {
                                _logger.LogDebug("[ProtocolService] Block [{Row},{Col}] '{Name}' bypass: {OldBypassed} -> {NewBypassed}", rowIndex, columnIndex, block.Name, block.Bypassed, bypassed);
                                return block with { Bypassed = bypassed };
                            }
                            return block;
                        }).ToList();

                        _grid = _grid.Select((r, i) => i == rowIndex ? r with { Blocks = updatedBlocks } : r).ToList();
                        _currentState = _currentState with { Grid = _grid, Timestamp = DateTime.UtcNow };
                    }
                    else
                    {
                        _logger.LogWarning("[ProtocolService] Invalid grid position: row={Row}, col={Col}", rowIndex, columnIndex);
                    }
                }

                _logger.LogDebug("[ProtocolService] Block bypass set");

                // Fire state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));

                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets a parameter value for a block.
        /// </summary>
        public async Task<bool> SetBlockParameterAsync(int rowIndex, int columnIndex, int paramIndex, float value)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("[ProtocolService] Cannot set block parameter - not connected");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting block parameter: row={Row}, col={Col}, param={Param}, value={Value}", rowIndex, columnIndex, paramIndex, value);

                byte[] message = ProtobufBuilder.BuildGridParamMessage(rowIndex, columnIndex, paramIndex, value);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send grid parameter message");
                    return false;
                }

                // Verify: grid edits always produce a Grid broadcast. Precise match on
                // chains[row].models[col].params[index] is the strongest signal; any
                // Grid echo means the edit was accepted. Treat a total absence as failure.
                if (!WaitForGridEcho(p => GridEchoConfirmsParam(p, rowIndex, columnIndex, paramIndex, value),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but param [{Row},{Col}]{Param}={Value} not matched — write may not have taken", rowIndex, columnIndex, paramIndex, value);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for param [{Row},{Col}]{Param} — write likely refused", rowIndex, columnIndex, paramIndex);
                    return false;
                }
                if (preciseMatch)
                {
                    _logger.LogDebug("[ProtocolService] Param [{Row},{Col}]{Param}={Value} confirmed by device echo", rowIndex, columnIndex, paramIndex, value);
                }

                // Update state optimistically - update the parameter value in the grid
                lock (_stateLock)
                {
                    if (_grid.Count > rowIndex)
                    {
                        GridRow row = _grid[rowIndex];
                        List<Block> updatedBlocks = row.Blocks.Select(block =>
                        {
                            if (block.SlotIndex == columnIndex)
                            {
                                List<BlockParam> updatedParams = block.Params.Select(p =>
                                    p.Index == paramIndex ? p with { Value = value } : p
                                ).ToList();
                                return block with { Params = updatedParams };
                            }
                            return block;
                        }).ToList();

                        _grid = _grid.Select((r, i) => i == rowIndex ? r with { Blocks = updatedBlocks } : r).ToList();
                        _currentState = _currentState with { Grid = _grid, Timestamp = DateTime.UtcNow };
                    }
                }

                _logger.LogDebug("[ProtocolService] Block parameter set");

                // Fire state change event
                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));

                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Places or replaces a block in a grid cell. Placement can be refused for DSP
        /// capacity with no error — verify by Grid echo (a refused block produces no echo).
        /// </summary>
        public async Task<bool> SetBlockAsync(int rowIndex, int columnIndex, uint modelHash)
        {
            if (!_isConnected) return LogNotConnected("set block");
            if (modelHash == 0) { _logger.LogWarning("[ProtocolService] Model hash 0 is not a valid block"); return false; }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting block: row={Row}, col={Col}, hash={Hash}", rowIndex, columnIndex, modelHash);

                byte[] message = ProtobufBuilder.BuildGridSetBlockMessage(rowIndex, columnIndex, modelHash);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send grid set-block message");
                    return false;
                }

                if (!WaitForGridEcho(p => GridEchoConfirmsBlock(p, rowIndex, columnIndex, modelHash),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but block [{Row},{Col}] not confirmed — likely DSP capacity refusal", rowIndex, columnIndex);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for block [{Row},{Col}] — likely DSP capacity refusal", rowIndex, columnIndex);
                    return false;
                }
                if (preciseMatch) _logger.LogDebug("[ProtocolService] Block [{Row},{Col}] hash={Hash} confirmed by device echo", rowIndex, columnIndex, modelHash);

                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Removes a block from a grid cell (Grid DELETE — an UPDATE with hash:0 is ignored).
        /// </summary>
        public async Task<bool> RemoveBlockAsync(int rowIndex, int columnIndex)
        {
            if (!_isConnected) return LogNotConnected("remove block");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Removing block: row={Row}, col={Col}", rowIndex, columnIndex);

                byte[] message = ProtobufBuilder.BuildGridRemoveBlockMessage(rowIndex, columnIndex);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send grid remove-block message");
                    return false;
                }

                if (!WaitForGridEcho(p => GridEchoConfirmsRemoved(p, rowIndex, columnIndex),
                                     out bool gridSeen, out _))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but removal [{Row},{Col}] not confirmed", rowIndex, columnIndex);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for removal [{Row},{Col}]", rowIndex, columnIndex);
                    return false;
                }
                _logger.LogDebug("[ProtocolService] Block [{Row},{Col}] removal confirmed by device echo", rowIndex, columnIndex);

                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Re-points a grid row's input to the given port id.
        /// </summary>
        public async Task<bool> SetChainInputAsync(int rowIndex, uint inPortId)
        {
            if (!_isConnected) return LogNotConnected("set chain input");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting chain input: row={Row}, port={Port}", rowIndex, inPortId);

                byte[] message = ProtobufBuilder.BuildGridChainInputMessage(rowIndex, inPortId);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send chain-input message");
                    return false;
                }

                if (!WaitForGridEcho(p => GridEchoConfirmsChainField(p, rowIndex, c => c.HasInPortid && c.InPortid == inPortId),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but input row={Row} not confirmed", rowIndex);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for chain input row={Row}", rowIndex);
                    return false;
                }
                if (preciseMatch) _logger.LogDebug("[ProtocolService] Chain input row={Row} -> {Port} confirmed by device echo", rowIndex, inPortId);

                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Re-points a grid row's output to the given port id (19 = MULTIPLE / Multi-Out).
        /// </summary>
        public async Task<bool> SetChainOutputAsync(int rowIndex, uint outPortId)
        {
            if (!_isConnected) return LogNotConnected("set chain output");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting chain output: row={Row}, port={Port}", rowIndex, outPortId);

                byte[] message = ProtobufBuilder.BuildGridChainOutputMessage(rowIndex, outPortId);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send chain-output message");
                    return false;
                }

                if (!WaitForGridEcho(p => GridEchoConfirmsChainField(p, rowIndex, c => c.HasOutPortid && c.OutPortid == outPortId),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but output row={Row} not confirmed", rowIndex);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for chain output row={Row}", rowIndex);
                    return false;
                }
                if (preciseMatch) _logger.LogDebug("[ProtocolService] Chain output row={Row} -> {Port} confirmed by device echo", rowIndex, outPortId);

                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Branches a row into its parallel lane (splitColumn/mixColumn). Pass mixColumn = -1
        /// for a never-rejoining branch; (-1, -1) clears the branch. Row must be 0 or 2.
        /// </summary>
        public async Task<bool> SetSplitAsync(int rowIndex, int splitColumn, int mixColumn)
        {
            if (!_isConnected) return LogNotConnected("set split");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogDebug("[ProtocolService] Setting split: row={Row}, split={Split}, mix={Mix}", rowIndex, splitColumn, mixColumn);

                byte[] message = ProtobufBuilder.BuildGridSplitMessage(rowIndex, splitColumn, mixColumn);
                if (!SendCommand(message, MessageTypes.Grid))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send split message");
                    return false;
                }

                if (!WaitForGridEcho(p => GridEchoConfirmsChainField(p, rowIndex,
                                     c => c.SplitControlPoints.Any(s => s.Split == splitColumn && s.Mix == mixColumn)),
                                     out bool gridSeen, out bool preciseMatch))
                {
                    if (gridSeen)
                        _logger.LogWarning("[ProtocolService] Grid echo seen but split row={Row} not confirmed", rowIndex);
                    else
                        _logger.LogWarning("[ProtocolService] No Grid echo within 2s for split row={Row}", rowIndex);
                    return false;
                }
                if (preciseMatch) _logger.LogDebug("[ProtocolService] Split row={Row} confirmed by device echo", rowIndex);

                FireStateChanged(StateUpdate.FromClient(_currentState, "grid"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Saves the preset currently on the grid into a setlist slot ("Save As").
        /// <paramref name="slot"/> is a linear index (0-255) or a slot name like "30A".
        /// The device de-duplicates names on collision and truncates to 20 chars, so the
        /// stored name may differ; a save is confirmed by a File listing echo carrying the
        /// slot index (best-effort — exact-name matching is unreliable because of renaming).
        /// </summary>
        public async Task<bool> SavePresetAsync(string setlistPath, string slot, string name, int instrument = 0)
        {
            if (!_isConnected) return LogNotConnected("save preset");
            if (string.IsNullOrWhiteSpace(setlistPath) || string.IsNullOrWhiteSpace(slot) || string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("[ProtocolService] setlistPath, slot and name are required to save");
                return false;
            }

            int slotIndex = int.TryParse(slot, out int parsed) ? parsed : ProtobufBuilder.SlotToPosition(slot);

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                _logger.LogInformation("[ProtocolService] Saving preset '{Name}' to {SetlistPath} slot {SlotIndex} (instrument={Instrument})...", name, setlistPath, slotIndex, instrument);

                byte[] message = ProtobufBuilder.BuildSavePresetMessage(setlistPath, slotIndex, name, instrument);
                if (!SendCommand(message, MessageTypes.File))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send save-preset message");
                    return false;
                }

                // Best-effort confirmation: the device lists the folder after a save. The
                // stored name may be renamed/de-duplicated, so match on the slot index only.
                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.File,
                    p => FileListingContainsSlot(p.Payload, setlistPath, slotIndex),
                    TimeSpan.FromSeconds(5));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Save sent but no listing echo for slot {SlotIndex} within 5s — check the setlist on the unit", slotIndex);
                }
                else
                {
                    _logger.LogInformation("[ProtocolService] Save confirmed by device listing (slot {SlotIndex})", slotIndex);
                }

                FireStateChanged(StateUpdate.FromClient(_currentState, "preset"));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error saving preset");
                return false;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        // ─── Phase 4: Global EQ / Master Volume / Tuner ────────────────────

        /// <summary>
        /// Sends a READ request for GlobalEQ, MasterVolume and Tuner so the
        /// device pushes its current state (mirrors pyquadcortex's
        /// <c>_read_state</c> — these are push-only fields the client never
        /// otherwise sees until something changes them). Fire-and-forget: the
        /// normal dispatch loop applies the resulting pushes via
        /// <see cref="HandleGlobalEqMessage"/>/<see cref="HandleMasterVolumeMessage"/>/
        /// <see cref="HandleTunerMessage"/> and fires the usual state-changed event.
        /// </summary>
        public bool RequestGlobalControlsRefresh()
        {
            if (!_isConnected) return LogNotConnected("refresh global controls");

            byte[] query = ProtobufBuilder.BuildStateQuery();
            bool ok = SendCommand(query, MessageTypes.GlobalEQ);
            ok &= SendCommand(query, MessageTypes.MasterVolume);
            ok &= SendCommand(query, MessageTypes.Tuner);
            return ok;
        }

        /// <summary>
        /// Sets one or more controls on a Global EQ band (1-5). Only the given
        /// controls are written (sparse writes, matching the device's own model).
        /// </summary>
        public async Task<bool> SetGlobalEqBandAsync(int band, float? gain = null, float? frequency = null,
            float? q = null, float? filterType = null, bool? enabled = null)
        {
            if (!_isConnected) return LogNotConnected("set global EQ band");
            if (band < 1 || band > ProtobufBuilder.GlobalEqBands)
            {
                _logger.LogWarning("[ProtocolService] Invalid EQ band {Band} (must be 1-{MaxBand})", band, ProtobufBuilder.GlobalEqBands);
                return false;
            }

            List<ParamWrite> writes = [];
            if (gain.HasValue) writes.Add(new ParamWrite(0, gain.Value));
            if (frequency.HasValue) writes.Add(new ParamWrite(1, frequency.Value));
            if (q.HasValue) writes.Add(new ParamWrite(2, q.Value));
            if (filterType.HasValue) writes.Add(new ParamWrite(3, filterType.Value));
            if (enabled.HasValue) writes.Add(new ParamWrite(4, enabled.Value ? 1f : 0f));

            if (writes.Count == 0)
            {
                _logger.LogWarning("[ProtocolService] SetGlobalEqBandAsync needs at least one control (gain/frequency/q/filterType/enabled)");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                bool anyConfirmed = false;
                foreach (ParamWrite write in writes)
                {
                    int paramIndex = ProtobufBuilder.GlobalEqBandParamIndex(band, write.Offset);
                    float value = write.Value;
                    byte[] message = ProtobufBuilder.BuildGlobalEqParamMessage(paramIndex, value);
                    if (!SendCommand(message, MessageTypes.GlobalEQ))
                    {
                        _logger.LogWarning("[ProtocolService] Failed to send GlobalEQ band {Band} param {Param}", band, paramIndex);
                        continue;
                    }

                    WirePayload? echo = _client.WaitForMessage(
                        MessageTypes.GlobalEQ,
                        p => GlobalEqEchoConfirmsParam(p.Payload, paramIndex, value),
                        TimeSpan.FromSeconds(2));

                    if (echo == null)
                        _logger.LogWarning("[ProtocolService] GlobalEQ band {Band} param {Param}={Value} sent but no device echo within 2s", band, paramIndex, value);
                    else
                        anyConfirmed = true;
                }

                if (anyConfirmed)
                    _logger.LogDebug("[ProtocolService] GlobalEQ band {Band} updated", band);
                else
                    _logger.LogWarning("[ProtocolService] GlobalEQ band {Band} — no writes confirmed", band);

                FireStateChanged(StateUpdate.FromClient(_currentState, "globalEq"));
                return anyConfirmed;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the Global EQ's OUT tab: overall output level and which output
        /// pairs it feeds.
        /// </summary>
        public async Task<bool> SetGlobalEqOutputAsync(float? level = null, bool? out12 = null, bool? out34 = null)
        {
            if (!_isConnected) return LogNotConnected("set global EQ output");

            List<ParamWrite> writes = [];
            if (level.HasValue) writes.Add(new ParamWrite(ProtobufBuilder.GlobalEqOutLevelIndex, level.Value));
            if (out12.HasValue) writes.Add(new ParamWrite(ProtobufBuilder.GlobalEqOut12Index, out12.Value ? 1f : 0f));
            if (out34.HasValue) writes.Add(new ParamWrite(ProtobufBuilder.GlobalEqOut34Index, out34.Value ? 1f : 0f));

            if (writes.Count == 0)
            {
                _logger.LogWarning("[ProtocolService] SetGlobalEqOutputAsync needs level, out12 or out34");
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                bool anyConfirmed = false;
                foreach (ParamWrite write in writes)
                {
                    int index = write.Offset;
                    float value = write.Value;
                    byte[] message = ProtobufBuilder.BuildGlobalEqParamMessage(index, value);
                    if (!SendCommand(message, MessageTypes.GlobalEQ)) continue;

                    WirePayload? echo = _client.WaitForMessage(
                        MessageTypes.GlobalEQ,
                        p => GlobalEqEchoConfirmsParam(p.Payload, index, value),
                        TimeSpan.FromSeconds(2));
                    if (echo != null) anyConfirmed = true;
                }

                if (anyConfirmed)
                    _logger.LogDebug("[ProtocolService] GlobalEQ output updated");
                else
                    _logger.LogWarning("[ProtocolService] GlobalEQ output — no writes confirmed");
                FireStateChanged(StateUpdate.FromClient(_currentState, "globalEq"));
                return anyConfirmed;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>Turns the Global EQ on/off. Bypassed=true is EQ OFF.</summary>
        public async Task<bool> SetGlobalEqBypassAsync(bool bypassed)
        {
            if (!_isConnected) return LogNotConnected("set global EQ bypass");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                byte[] message = ProtobufBuilder.BuildGlobalEqBypassMessage(bypassed);
                if (!SendCommand(message, MessageTypes.GlobalEQ))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send GlobalEQ bypass message");
                    return false;
                }

                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.GlobalEQ,
                    p => TryParseGlobalEq(p.Payload, out GlobalEQMessage? m) && m!.HasBypassed && m.Bypassed == bypassed,
                    TimeSpan.FromSeconds(2));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] GlobalEQ bypass={Bypassed} sent but no device echo within 2s", bypassed);
                    return false;
                }

                _logger.LogDebug("[ProtocolService] GlobalEQ bypass set to {Bypassed}", bypassed);
                FireStateChanged(StateUpdate.FromClient(_currentState, "globalEq"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the Master Volume, normalized 0..1. The device's own read-back is
        /// stale immediately after a write (a documented pyquadcortex gotcha), so
        /// this waits for a broadcast actually carrying the new value rather than
        /// trusting the first echo seen.
        /// </summary>
        public async Task<bool> SetMasterVolumeAsync(float volume)
        {
            if (!_isConnected) return LogNotConnected("set master volume");
            if (volume < 0f || volume > 1f)
            {
                _logger.LogWarning("[ProtocolService] Master volume must be 0..1, got {Volume}", volume);
                return false;
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                byte[] message = ProtobufBuilder.BuildMasterVolumeMessage(volume);
                if (!SendCommand(message, MessageTypes.MasterVolume))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send master volume message");
                    return false;
                }

                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.MasterVolume,
                    p =>
                    {
                        try
                        {
                            // The physical knob quantizes to steps of ~1/121 (pyquadcortex),
                            // so the device's own reported value can legitimately differ
                            // from the exact float we sent — tolerance is half a step.
                            MasterVolumeMessage m = MasterVolumeMessage.Parser.ParseFrom(p.Payload);
                            return m.HasVolume && Math.Abs(m.Volume - volume) < (1f / 121f / 2f);
                        }
                        catch { return false; }
                    },
                    TimeSpan.FromSeconds(3));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Master volume {Volume:0.###} sent but no device echo within 3s", volume);
                    return false;
                }

                _logger.LogDebug("[ProtocolService] Master volume set to {Volume:0.###} (device echo confirmed)", volume);
                FireStateChanged(StateUpdate.FromClient(_currentState, "masterVolume"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Selects which input feeds the Tuner.
        ///
        /// WARNING (pyquadcortex-documented): this invisibly ENGAGES the tuner —
        /// nothing changes on screen. If the mute preference is already true, the
        /// outputs go silent with no visible cause. Call <see cref="RestoreAudioAsync"/>
        /// afterward if you also touch mute. Only INPUT_1/2, RETURN_1/2, INPUT_1_2,
        /// USB_5/6 are accepted; everything else (including RETURN_1_2) is silently
        /// refused and the setting reverts.
        /// </summary>
        public async Task<bool> SetTunerInputAsync(int inputPortId)
        {
            if (!_isConnected) return LogNotConnected("set tuner input");

            _logger.LogWarning("[ProtocolService] Writing to the Tuner invisibly ENGAGES it — nothing changes on screen. If mute is already on, this silences the outputs with no visible cause; call RestoreAudioAsync() when done.");

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                byte[] message = ProtobufBuilder.BuildTunerInputMessage(inputPortId);
                if (!SendCommand(message, MessageTypes.Tuner))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send tuner input message");
                    return false;
                }

                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.Tuner,
                    p =>
                    {
                        try
                        {
                            TunerMessage m = TunerMessage.Parser.ParseFrom(p.Payload);
                            return m.HasInputPortId && m.InputPortId == inputPortId;
                        }
                        catch { return false; }
                    },
                    TimeSpan.FromSeconds(2));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Tuner input {InputPortId} sent but no device echo within 2s (some inputs are silently refused)", inputPortId);
                    return false;
                }

                _logger.LogDebug("[ProtocolService] Tuner input set to {InputPortId} (device echo confirmed)", inputPortId);
                FireStateChanged(StateUpdate.FromClient(_currentState, "tuner"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets the Tuner's mute-while-tuning preference.
        ///
        /// WARNING (pyquadcortex-documented): writing this invisibly ENGAGES the
        /// tuner. If <paramref name="mute"/> is true, the outputs go silent the
        /// instant this lands, with nothing on screen to explain it, and the state
        /// can persist across recalls/saves/scene switches. The only lossless
        /// release is a person opening and closing the tuner on the unit; from the
        /// host, <see cref="RestoreAudioAsync"/> clears the preference (audible
        /// again, but the player's silent-tuning preference is lost).
        /// </summary>
        public async Task<bool> SetTunerMuteAsync(bool mute)
        {
            if (!_isConnected) return LogNotConnected("set tuner mute");

            if (mute)
            {
                _logger.LogWarning("[ProtocolService] Engaging tuner mute SILENCES the outputs with nothing on screen to explain it. Call RestoreAudioAsync() to undo, or have someone close the tuner on the unit.");
            }

            await _operationSemaphore.WaitAsync(_cts.Token);
            try
            {
                byte[] message = ProtobufBuilder.BuildTunerMuteMessage(mute);
                if (!SendCommand(message, MessageTypes.Tuner))
                {
                    _logger.LogWarning("[ProtocolService] Failed to send tuner mute message");
                    return false;
                }

                WirePayload? echo = _client.WaitForMessage(
                    MessageTypes.Tuner,
                    p =>
                    {
                        try
                        {
                            TunerMessage m = TunerMessage.Parser.ParseFrom(p.Payload);
                            return m.HasMute && m.Mute == mute;
                        }
                        catch { return false; }
                    },
                    TimeSpan.FromSeconds(2));

                if (echo == null)
                {
                    _logger.LogWarning("[ProtocolService] Tuner mute={Mute} sent but no device echo within 2s", mute);
                    return false;
                }

                _logger.LogDebug("[ProtocolService] Tuner mute set to {Mute} (device echo confirmed)", mute);
                FireStateChanged(StateUpdate.FromClient(_currentState, "tuner"));
                return true;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Undoes the silence a host Tuner write can cause: clears the mute
        /// preference if currently set (leaves the unit engaged-but-audible).
        /// Mirrors pyquadcortex's <c>restore_audio()</c>. There is no message that
        /// closes the tuner from the host — only a person on the unit can do that
        /// losslessly (keeping the preference intact for next time).
        /// </summary>
        public async Task<bool> RestoreAudioAsync()
        {
            if (!_isConnected) return LogNotConnected("restore audio");

            bool currentlyMuted;
            lock (_stateLock) { currentlyMuted = _currentState.Tuner.Mute; }
            if (!currentlyMuted)
            {
                _logger.LogDebug("[ProtocolService] Restore audio: tuner mute preference already off, nothing to do");
                return false;
            }

            _logger.LogInformation("[ProtocolService] Restoring audio — clearing tuner mute preference");
            return await SetTunerMuteAsync(false);
        }

        private static bool GlobalEqEchoConfirmsParam(byte[] payload, int paramIndex, float value)
        {
            return TryParseGlobalEq(payload, out GlobalEQMessage? m)
                && m!.Parameters.Any(p => p.ParameterIndex == paramIndex && Math.Abs(p.Value - value) < 0.001f);
        }

        private static bool TryParseGlobalEq(byte[] payload, out GlobalEQMessage? message)
        {
            try { message = GlobalEQMessage.Parser.ParseFrom(payload); return true; }
            catch { message = null; return false; }
        }

        private bool LogNotConnected(string op)
        {
            _logger.LogWarning("[ProtocolService] Cannot {Operation} - not connected", op);
            return false;
        }

        // ─── Grid write verification helpers ───────────────────────────

        /// <summary>
        /// Waits for a Grid broadcast after a grid write. Returns true when the
        /// <paramref name="confirm"/> predicate matches an echo; <paramref name="gridSeen"/>
        /// records that any Grid echo arrived (acceptance), <paramref name="preciseMatch"/>
        /// that one matched the value we sent. Caller holds the operation semaphore.
        /// </summary>
        private bool WaitForGridEcho(Func<byte[], bool> confirm, out bool gridSeen, out bool preciseMatch)
        {
            gridSeen = false;
            preciseMatch = false;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                WirePayload? echo = _client.WaitForMessage(MessageTypes.Grid, _ => true, TimeSpan.FromMilliseconds(100));
                if (echo == null) continue;
                gridSeen = true;
                if (confirm(echo.Payload))
                {
                    preciseMatch = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether a Grid echo confirms bypass[row][col] == <paramref name="bypassed"/>.
        /// Row/column may arrive without field presence, in which case the index in the
        /// repeated field is the position (same convention as pyquadcortex's blocks()).
        /// </summary>
        private bool GridEchoConfirmsBypass(byte[] payload, int row, int col, bool bypassed)
        {
            try
            {
                GridMessage msg = GridMessage.Parser.ParseFrom(payload);
                for (int i = 0; i < msg.Preset.Bypass.Count; i++)
                {
                    Bypass byp = msg.Preset.Bypass[i];
                    if ((byp.HasRow ? byp.Row : (uint)i) != row) continue;
                    for (int j = 0; j < byp.ColBypass.Count; j++)
                    {
                        ColBypass cb = byp.ColBypass[j];
                        if ((cb.HasColumn ? cb.Column : (uint)j) != col) continue;
                        return cb.SceneBypass.Any(sb => sb.Bypass == bypassed);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing grid echo (bypass)");
            }
            return false;
        }

        /// <summary>
        /// Whether a Grid echo confirms chains[row].models[col].params[paramIndex] holds
        /// approximately <paramref name="value"/>. Same presence-fallback convention as
        /// <see cref="GridEchoConfirmsBypass"/>.
        /// </summary>
        private bool GridEchoConfirmsParam(byte[] payload, int row, int col, int paramIndex, float value)
        {
            try
            {
                GridMessage msg = GridMessage.Parser.ParseFrom(payload);
                for (int i = 0; i < msg.Preset.Chains.Count; i++)
                {
                    Chain ch = msg.Preset.Chains[i];
                    if ((ch.HasRow ? ch.Row : (uint)i) != row) continue;
                    for (int j = 0; j < ch.Models.Count; j++)
                    {
                        Model mdl = ch.Models[j];
                        if ((mdl.HasColumn ? mdl.Column : (uint)j) != col) continue;
                        foreach (Param p in mdl.Params)
                        {
                            if (p.HasIndex && p.Index != paramIndex) continue;
                            if (p.ParamValues.Count == 0) continue;
                            ParamValue v = p.ParamValues[0];
                            if (v.HasFloatValue) return Math.Abs(v.FloatValue - value) < 0.001f;
                            if (v.HasIntValue) return v.IntValue == (int)value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing grid echo (param)");
            }
            return false;
        }

        /// <summary>
        /// Whether a Grid echo confirms a block with <paramref name="modelHash"/> at [row][col].
        /// </summary>
        private bool GridEchoConfirmsBlock(byte[] payload, int row, int col, uint modelHash)
        {
            try
            {
                GridMessage msg = GridMessage.Parser.ParseFrom(payload);
                for (int i = 0; i < msg.Preset.Chains.Count; i++)
                {
                    Chain ch = msg.Preset.Chains[i];
                    if ((ch.HasRow ? ch.Row : (uint)i) != row) continue;
                    for (int j = 0; j < ch.Models.Count; j++)
                    {
                        Model mdl = ch.Models[j];
                        if ((mdl.HasColumn ? mdl.Column : (uint)j) != col) continue;
                        return mdl.HasHash && mdl.Hash == modelHash;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing grid echo (block)");
            }
            return false;
        }

        /// <summary>
        /// Whether a Grid echo confirms the block at [row][col] is gone (cell absent or hash zeroed).
        /// </summary>
        private bool GridEchoConfirmsRemoved(byte[] payload, int row, int col)
        {
            try
            {
                GridMessage msg = GridMessage.Parser.ParseFrom(payload);
                for (int i = 0; i < msg.Preset.Chains.Count; i++)
                {
                    Chain ch = msg.Preset.Chains[i];
                    if ((ch.HasRow ? ch.Row : (uint)i) != row) continue;
                    for (int j = 0; j < ch.Models.Count; j++)
                    {
                        Model mdl = ch.Models[j];
                        if ((mdl.HasColumn ? mdl.Column : (uint)j) != col) continue;
                        return mdl.HasHash && mdl.Hash == 0;
                    }
                    return true; // chain has no model at the target column → removed
                }
                return true; // chain gone entirely → removed
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing grid echo (remove)");
            }
            return false;
        }

        /// <summary>
        /// Whether a Grid echo carries chain <paramref name="row"/> satisfying <paramref name="check"/>
        /// (used for in_portid / out_portid / split_control_points confirmations).
        /// </summary>
        private bool GridEchoConfirmsChainField(byte[] payload, int row, Func<Chain, bool> check)
        {
            try
            {
                GridMessage msg = GridMessage.Parser.ParseFrom(payload);
                for (int i = 0; i < msg.Preset.Chains.Count; i++)
                {
                    Chain ch = msg.Preset.Chains[i];
                    if ((ch.HasRow ? ch.Row : (uint)i) != row) continue;
                    if (check(ch)) return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing grid echo (chain)");
            }
            return false;
        }

        /// <summary>
        /// Whether a File message lists a folder for <paramref name="setlistPath"/> containing
        /// a preset at <paramref name="slotIndex"/> (used to confirm a save).
        /// </summary>
        private static bool FileListingContainsSlot(byte[] payload, string setlistPath, int slotIndex)
        {
            try
            {
                FileMessage msg = FileMessage.Parser.ParseFrom(payload);
                if (msg.Folder == null) return false;
                if (!string.Equals(msg.Folder.Key, setlistPath, StringComparison.OrdinalIgnoreCase)) return false;
                return msg.Folder.Files.Any(f => f.HasIndex && f.Index == slotIndex);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Background thread that processes incoming messages from the protocol handler.
        /// Updates state cache and fires events.
        /// </summary>
        private void MessageProcessorLoop()
        {
            _logger.LogDebug("[ProtocolService] Message processor thread started");

            Dictionary<uint, int> messageCounters = new();

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_incomingMessages.TryDequeue(out WirePayload? message))
                    {
                        // Count message types to see patterns
                        messageCounters[message.MessageType] = messageCounters.GetValueOrDefault(message.MessageType, 0) + 1;

                        // Log message frequency every 100 messages
                        if (messageCounters.Values.Sum() % 100 == 0)
                        {
                            _logger.LogDebug("[ProtocolService] Message counts: {Counts}", string.Join(", ", messageCounters.Select(kv => $"{kv.Key}:{kv.Value}")));
                        }

                        ProcessIncomingMessage(message);
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProtocolService] Error processing message");
                }
            }

            _logger.LogDebug("[ProtocolService] Message processor thread stopped");
        }

        /// <summary>
        /// Processes an incoming message and updates state cache.
        /// </summary>
        private void ProcessIncomingMessage(WirePayload message)
        {
            _logger.LogDebug("[ProtocolService] Received message type {MessageType}, payload: {ByteCount} bytes", message.MessageType, message.Payload.Length);

            bool stateChanged;
            lock (_stateLock)
            {
                stateChanged = DispatchMessage(message);
            }

            if (stateChanged)
            {
                _logger.LogDebug("[ProtocolService] State changed, firing event: Scene={Scene}, BPM={Bpm}, Mode={Mode}", _currentState.Scene, _currentState.Bpm, _currentState.Mode);
                FireStateChanged(StateUpdate.FromDevice(_currentState));
            }
        }

        private bool DispatchMessage(WirePayload message)
        {
            return message.MessageType switch
            {
                MessageTypes.Scene => HandleSceneMessage(message),
                MessageTypes.Mode => HandleModeMessage(message),
                MessageTypes.Tempo => HandleTempoMessage(message),
                MessageTypes.SetlistPosition => HandleSetlistPosition(message),
                MessageTypes.RecallPreset => HandleRecallPreset(message),
                MessageTypes.Grid => HandleGridMessage(message),
                MessageTypes.NewModels => HandleNewModels(),
                MessageTypes.ModelRepo => HandleModelRepo(message),
                MessageTypes.File => HandleFileMessage(message),
                MessageTypes.Version => HandleVersionMessage(message),
                MessageTypes.Connection => HandleConnectionMessage(message),
                MessageTypes.KeepAlive => false,
                MessageTypes.PresetDirty => HandlePresetDirty(),
                MessageTypes.GlobalEQ => HandleGlobalEqMessage(message),
                MessageTypes.MasterVolume => HandleMasterVolumeMessage(message),
                MessageTypes.Tuner => HandleTunerMessage(message),
                _ => HandleUnknownMessage(message)
            };
        }

        private bool HandleGlobalEqMessage(WirePayload message)
        {
            try
            {
                GlobalEQMessage msg = GlobalEQMessage.Parser.ParseFrom(message.Payload);
                GlobalEqState eq = _currentState.GlobalEq;
                List<GlobalEqBand> bands = eq.Bands.Count == ProtobufBuilder.GlobalEqBands
                    ? [.. eq.Bands]
                    : [.. Enumerable.Range(1, ProtobufBuilder.GlobalEqBands).Select(b => new GlobalEqBand { Band = b })];

                float outputLevel = eq.OutputLevel;
                bool out12 = eq.Out12;
                bool out34 = eq.Out34;

                foreach (GlobalEQParameter p in msg.Parameters)
                {
                    if (p.ParameterIndex == ProtobufBuilder.GlobalEqOutLevelIndex) { outputLevel = p.Value; continue; }
                    if (p.ParameterIndex == ProtobufBuilder.GlobalEqOut12Index) { out12 = p.Value != 0f; continue; }
                    if (p.ParameterIndex == ProtobufBuilder.GlobalEqOut34Index) { out34 = p.Value != 0f; continue; }

                    int band = p.ParameterIndex / ProtobufBuilder.GlobalEqBandStride + 1;
                    int offset = p.ParameterIndex % ProtobufBuilder.GlobalEqBandStride;
                    if (band < 1 || band > ProtobufBuilder.GlobalEqBands) continue;

                    GlobalEqBand current = bands[band - 1];
                    bands[band - 1] = offset switch
                    {
                        0 => current with { Gain = p.Value },
                        1 => current with { Frequency = p.Value },
                        2 => current with { Q = p.Value },
                        3 => current with { FilterType = p.Value },
                        4 => current with { Enabled = p.Value != 0f },
                        _ => current
                    };
                }

                bool bypassed = msg.HasBypassed ? msg.Bypassed : eq.Bypassed;

                GlobalEqState updated = eq with { Bypassed = bypassed, OutputLevel = outputLevel, Out12 = out12, Out34 = out34, Bands = bands };
                if (updated == eq) return false;
                _currentState = _currentState with { GlobalEq = updated, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] GlobalEQ state updated");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing GlobalEQ message");
                return false;
            }
        }

        private bool HandleMasterVolumeMessage(WirePayload message)
        {
            try
            {
                MasterVolumeMessage msg = MasterVolumeMessage.Parser.ParseFrom(message.Payload);
                if (!msg.HasVolume) return false;
                if (Math.Abs(msg.Volume - _currentState.MasterVolume.Volume) < 0.0005f) return false;

                _currentState = _currentState with { MasterVolume = new MasterVolumeState { Volume = msg.Volume }, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] MasterVolume updated to {Volume:0.###}", msg.Volume);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing MasterVolume message");
                return false;
            }
        }

        private bool HandleTunerMessage(WirePayload message)
        {
            try
            {
                TunerMessage msg = TunerMessage.Parser.ParseFrom(message.Payload);
                TunerState current = _currentState.Tuner;
                // The device reports Infinity/NaN for "no pitch detected" (silence, muted
                // input). Caching that raw would poison every future state broadcast: once
                // an Infinity lands in _currentState, System.Text.Json refuses to serialize
                // any message containing it, and clients silently stop getting responses.
                bool validFrequency = msg.HasFrequency && float.IsFinite(msg.Frequency);
                TunerState updated = current with
                {
                    InputPortId = msg.HasInputPortId ? msg.InputPortId : current.InputPortId,
                    Mute = msg.HasMute ? msg.Mute : current.Mute,
                    Frequency = validFrequency ? msg.Frequency : current.Frequency
                };
                if (updated == current) return false;

                _currentState = _currentState with { Tuner = updated, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] Tuner state updated: input={InputPortId}, mute={Mute}", updated.InputPortId, updated.Mute);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing Tuner message");
                return false;
            }
        }

        private bool HandleSceneMessage(WirePayload message)
        {
            int scene = ParseSceneMessage(message.Payload);
            _logger.LogDebug("[ProtocolService] Parsed scene value: {Scene}, current: {CurrentScene}", scene, _currentState.Scene);
            if (scene >= 0 && scene != _currentState.Scene)
            {
                _logger.LogInformation("[ProtocolService] Scene change: {OldScene} -> {NewScene}", _currentState.Scene, scene);
                _currentState = _currentState with { Scene = scene, Timestamp = DateTime.UtcNow };
                if (_currentPreset != null)
                {
                    int bpm = ExtractBpmFromPreset(_currentPreset, scene);
                    _grid = BuildGrid(_currentPreset, scene);
                    _currentState = _currentState with
                    {
                        Grid = _grid,
                        Bpm = bpm > 0 ? bpm : _currentState.Bpm,
                        Timestamp = DateTime.UtcNow
                    };
                    if (bpm > 0) _logger.LogDebug("[ProtocolService] BPM from preset scene={Scene}: {Bpm}", scene, bpm);
                }
                _logger.LogDebug("[ProtocolService] Scene updated to {Scene}", scene);
                return true;
            }
            if (scene < 0)
            {
                _logger.LogWarning("[ProtocolService] Failed to parse scene from payload: {Payload}", Convert.ToHexString(message.Payload));
            }
            return false;
        }

        private bool HandleModeMessage(WirePayload message)
        {
            int mode = ParseModeMessage(message.Payload);
            if (mode >= 0 && mode != _currentState.Mode)
            {
                _currentState = _currentState with { Mode = mode, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] Mode updated to {Mode}", DeviceMode.GetModeName(mode));
                return true;
            }
            return false;
        }

        private bool HandleTempoMessage(WirePayload message)
        {
            int maybeBpm = ParseTempoMessage(message.Payload);

            // Complete a pending tempo write verification if the device echoes
            // back the value we just sent.
            if (_tempoEchoTcs != null && maybeBpm > 0 && maybeBpm == _expectedTempoBpm)
            {
                _tempoEchoTcs.TrySetResult(true);
            }

            if (maybeBpm > 0 && maybeBpm != _currentState.Bpm)
            {
                _currentState = _currentState with { Bpm = maybeBpm, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] BPM updated to {Bpm}", maybeBpm);
                return true;
            }
            return false;
        }

        private bool HandleSetlistPosition(WirePayload message)
        {
            PresetInfo? presetInfo = ParseSetlistPosition(message.Payload);
            if (presetInfo != null)
            {
                _currentState = _currentState with { CurrentPreset = presetInfo, Timestamp = DateTime.UtcNow };
                _logger.LogDebug("[ProtocolService] Preset position updated to {PresetIndex}", presetInfo.PresetIndex);
                return true;
            }
            return false;
        }

        private bool HandleRecallPreset(WirePayload message)
        {
            BinaryPreset? preset = ParseRecallPreset(message.Payload);
            if (preset == null) return false;

            _currentPreset = preset;
            PresetDetails? details = BuildPresetDetails(preset);
            _grid = BuildGrid(preset, _currentState.Scene);

            // Extract BPM from preset's scene_tempo for the current scene
            int bpm = ExtractBpmFromPreset(preset, _currentState.Scene);

            _currentState = _currentState with
            {
                PresetDetails = details,
                Grid = _grid,
                Bpm = bpm > 0 ? bpm : _currentState.Bpm,
                Timestamp = DateTime.UtcNow
            };
            _logger.LogDebug("[ProtocolService] RecallPreset updated, BPM={Bpm}", bpm);
            return true;
        }

        private bool HandleGridMessage(WirePayload message)
        {
            BinaryPreset? gridEcho = ParseGridMessage(message.Payload);
            if (gridEcho == null) return false;

            // A Grid push is a SPARSE echo of only what changed (e.g. one bypass
            // entry, or one row's one model) — not a full preset. Wholesale
            // replacing _currentPreset with it (as this used to do) discards
            // every other row/block/split the cache knew about, collapsing the
            // grid to almost nothing after the very first confirmed write.
            if (_currentPreset == null)
            {
                _currentPreset = gridEcho;
            }
            else
            {
                MergeGridEcho(_currentPreset, gridEcho);
            }

            _grid = BuildGrid(_currentPreset, _currentState.Scene);
            _currentState = _currentState with { Grid = _grid, Timestamp = DateTime.UtcNow };
            _logger.LogDebug("[ProtocolService] Grid updated");
            return true;
        }

        /// <summary>
        /// Merges a sparse Grid echo (bypass and/or chain deltas) into the cached
        /// preset in place, keyed by row/column, instead of replacing whole
        /// collections. Presence-fallback (index-as-position) mirrors the same
        /// convention used by the echo-confirmation predicates.
        /// </summary>
        private static void MergeGridEcho(BinaryPreset preset, BinaryPreset echo)
        {
            for (int i = 0; i < echo.Bypass.Count; i++)
            {
                MergeBypass(preset, echo.Bypass[i], i);
            }
            for (int i = 0; i < echo.Chains.Count; i++)
            {
                MergeChain(preset, echo.Chains[i], i);
            }
        }

        private static void MergeBypass(BinaryPreset preset, Bypass incoming, int incomingIndex)
        {
            uint row = incoming.HasRow ? incoming.Row : (uint)incomingIndex;
            Bypass? existing = preset.Bypass.FirstOrDefault(b => (b.HasRow ? b.Row : (uint)preset.Bypass.IndexOf(b)) == row);
            if (existing == null)
            {
                preset.Bypass.Add(incoming);
                return;
            }

            for (int j = 0; j < incoming.ColBypass.Count; j++)
            {
                ColBypass incomingCol = incoming.ColBypass[j];
                uint column = incomingCol.HasColumn ? incomingCol.Column : (uint)j;
                ColBypass? existingCol = existing.ColBypass.FirstOrDefault(c =>
                    (c.HasColumn ? c.Column : (uint)existing.ColBypass.IndexOf(c)) == column);

                if (existingCol == null)
                {
                    existing.ColBypass.Add(incomingCol);
                    continue;
                }

                if (incomingCol.SceneBypass.Count > 0)
                {
                    existingCol.SceneBypass.Clear();
                    existingCol.SceneBypass.AddRange(incomingCol.SceneBypass);
                }
                if (incomingCol.HasSceneMode) existingCol.SceneMode = incomingCol.SceneMode;
            }
        }

        private static void MergeChain(BinaryPreset preset, Chain incoming, int incomingIndex)
        {
            uint row = incoming.HasRow ? incoming.Row : (uint)incomingIndex;
            Chain? existing = preset.Chains.FirstOrDefault(c => (c.HasRow ? c.Row : (uint)preset.Chains.IndexOf(c)) == row);
            if (existing == null)
            {
                preset.Chains.Add(incoming);
                return;
            }

            if (incoming.HasInPortid) existing.InPortid = incoming.InPortid;
            if (incoming.HasOutPortid) existing.OutPortid = incoming.OutPortid;

            if (incoming.SplitControlPoints.Count > 0)
            {
                existing.SplitControlPoints.Clear();
                existing.SplitControlPoints.AddRange(incoming.SplitControlPoints);
            }

            for (int j = 0; j < incoming.Models.Count; j++)
            {
                Model incomingModel = incoming.Models[j];
                uint column = incomingModel.HasColumn ? incomingModel.Column : (uint)j;
                Model? existingModel = existing.Models.FirstOrDefault(m =>
                    (m.HasColumn ? m.Column : (uint)existing.Models.IndexOf(m)) == column);

                if (existingModel == null)
                {
                    existing.Models.Add(incomingModel);
                    continue;
                }

                if (incomingModel.HasHash) existingModel.Hash = incomingModel.Hash;

                for (int k = 0; k < incomingModel.Params.Count; k++)
                {
                    Param incomingParam = incomingModel.Params[k];
                    Param? existingParam = existingModel.Params.FirstOrDefault(p =>
                        (p.HasIndex ? p.Index : (uint)existingModel.Params.IndexOf(p)) == (incomingParam.HasIndex ? incomingParam.Index : (uint)k));

                    if (existingParam == null)
                    {
                        existingModel.Params.Add(incomingParam);
                    }
                    else if (incomingParam.ParamValues.Count > 0)
                    {
                        existingParam.ParamValues.Clear();
                        existingParam.ParamValues.AddRange(incomingParam.ParamValues);
                    }
                }
            }
        }

        /// <summary>
        /// Device→Host notification that new model IDs exist (e.g. the user just saved a
        /// Neural Capture). The ModelRepo snapshot fetched at handshake won't know these
        /// IDs' names, so re-request it — the response is routed back through
        /// <see cref="HandleModelRepo"/> like any other message.
        /// </summary>
        private bool HandleNewModels()
        {
            _logger.LogDebug("[ProtocolService] NewModels notification received - re-requesting ModelRepo");
            SendCommand(ProtocolMessages.BuildModelRepoRequest(), MessageTypes.ModelRepo);
            return false;
        }

        private bool HandleModelRepo(WirePayload message)
        {
            Dictionary<int, ModelInfo> modelMap = ParseModelRepo(message.Payload);
            if (modelMap.Count == 0) return false;

            _modelMap = modelMap;
            _currentState = _currentState with { ModelMap = _modelMap, Timestamp = DateTime.UtcNow };
            if (_currentPreset != null)
            {
                _grid = BuildGrid(_currentPreset, _currentState.Scene);
                _currentState = _currentState with { Grid = _grid, Timestamp = DateTime.UtcNow };
            }
            _logger.LogDebug("[ProtocolService] ModelRepo parsed: {ModelCount} models", _modelMap.Count);
            return true;
        }

        private bool HandleFileMessage(WirePayload message)
        {
            _fileMessages.Add(message.Payload);

            // Throttle: rebuild at most once per 500ms to avoid hammering _stateLock
            // during an initial flood of file messages (the final rebuild is guaranteed
            // by RequestPresetLibraryAsync after the poll loop finishes).
            if ((DateTime.UtcNow - _lastLibraryRebuild).TotalMilliseconds >= 500)
            {
                RebuildPresetLibrary();
            }

            return false;
        }

        private bool HandleVersionMessage(WirePayload message)
        {
            DeviceInfo? deviceInfo = ParseVersion(message.Payload);
            if (deviceInfo == null) return false;

            _currentState = _currentState with { DeviceInfo = deviceInfo, Timestamp = DateTime.UtcNow };
            return true;
        }

        private bool HandleConnectionMessage(WirePayload message)
        {
            DeviceInfo? connectionInfo = ParseConnection(message.Payload);
            if (connectionInfo == null) return false;

            _currentState = _currentState with { DeviceInfo = connectionInfo, Timestamp = DateTime.UtcNow };
            return true;
        }

        private bool HandlePresetDirty()
        {
            _logger.LogDebug("[ProtocolService] PresetDirty notification - hardware changes detected");
            _logger.LogDebug("[ProtocolService] Re-querying scene state due to PresetDirty...");

            _ = Task.Run(async () =>
            {
                try
                {
                    await QueryStateFieldAsync(MessageTypes.Scene, "Scene", TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProtocolService] Error querying scene after PresetDirty");
                }
            });
            return false;
        }

        private bool HandleUnknownMessage(WirePayload message)
        {
            _logger.LogWarning("[ProtocolService] Unhandled message type {MessageType} - payload: {Payload}...", message.MessageType, Convert.ToHexString(message.Payload.Take(20).ToArray()));
            if (message.MessageType == 34)
            {
                _logger.LogDebug("[ProtocolService] PRESET DIRTY notification received - might indicate scene change");
            }
            return false;
        }

        private PresetInfo? ParseSetlistPosition(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                SetlistPositionMessage message = SetlistPositionMessage.Parser.ParseFrom(data);
                return new PresetInfo
                {
                    SetlistPath = message.FolderKey ?? string.Empty,
                    PresetIndex = (int)message.Position,
                    IsFactory = message.IsFactory
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing SetlistPosition");
                return null;
            }
        }

        private PresetDetails? BuildPresetDetails(BinaryPreset preset)
        {
            return new PresetDetails
            {
                Name = preset.Name ?? string.Empty,
                Author = preset.AuthorName ?? string.Empty,
                Uuid = preset.AuthorId ?? string.Empty,
                Created = preset.Date ?? string.Empty,
                FwVersion = preset.CreatedVersion.FirstOrDefault() ?? string.Empty,
                Scenes = preset.SceneLabels.ToList()
            };
        }

        private BinaryPreset? ParseRecallPreset(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                RecallPresetMessage message = RecallPresetMessage.Parser.ParseFrom(data);
                return message.Preset;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing RecallPreset");
                return null;
            }
        }

        private BinaryPreset? ParseGridMessage(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                GridMessage message = GridMessage.Parser.ParseFrom(data);
                return message.Preset;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing Grid");
                return null;
            }
        }

        private Dictionary<int, ModelInfo> ParseModelRepo(byte[] payload)
        {
            try
            {
                return ModelCatalog.Parse(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing ModelRepo");
                return [];
            }
        }

        private DeviceInfo? ParseVersion(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                VersionMessage message = VersionMessage.Parser.ParseFrom(data);
                return new DeviceInfo
                {
                    FirmwareVersion = message.AppFwVersion ?? string.Empty,
                    ProtocolVersion = message.CommsVersion.ToStringUtf8(),
                    SerialNumber = message.DeviceSerialNumber ?? string.Empty,
                    MacAddress = string.Empty,
                    Name = "Quad Cortex",
                    IsConnected = _isConnected
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing Version");
                return null;
            }
        }

        private DeviceInfo? ParseConnection(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                ConnectionMessage message = ConnectionMessage.Parser.ParseFrom(data);
                DeviceInfo existing = _currentState.DeviceInfo ?? new DeviceInfo();
                return existing with { IsConnected = message.Connected };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing Connection");
                return null;
            }
        }

        private List<GridRow> BuildGrid(BinaryPreset preset, int currentScene)
        {
            Dictionary<int, Dictionary<int, Dictionary<int, bool>>> sceneBypasses = BuildSceneBypassMap(preset);
            Dictionary<int, GridRow> rows = new();
            int rowFallback = 0;

            foreach (Chain? chain in preset.Chains)
            {
                int rowIndex = chain.HasRow ? (int)chain.Row : rowFallback++;
                (List<Block>? blocks, List<SplitInfo>? splits) = BuildChainBlocks(chain, rowIndex, currentScene, sceneBypasses);
                ChainIO io = BuildChainIo(chain);

                rows[rowIndex] = new GridRow
                {
                    Blocks = blocks,
                    Input = io.Input,
                    Output = io.Output,
                    InPortId = io.InPortId,
                    OutPortId = io.OutPortId,
                    Splits = splits
                };
            }

            return rows
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToList();
        }

        private ChainBlocksResult BuildChainBlocks(Chain chain, int rowIndex, int currentScene,
            Dictionary<int, Dictionary<int, Dictionary<int, bool>>> sceneBypasses)
        {
            List<Block> blocks = new();

            for (int modelPos = 0; modelPos < chain.Models.Count; modelPos++)
            {
                Model model = chain.Models[modelPos];
                if (!model.HasHash) continue;

                int modelId = (int)model.Hash;
                // Column is frequently absent on a full preset dump (only echoes
                // reliably set it) — array position is the device's own fallback
                // convention, same as the echo-confirmation predicates use.
                int slotIndex = model.HasColumn ? (int)model.Column : modelPos;
                ModelInfo? info = _modelMap.TryGetValue(modelId, out ModelInfo? modelInfo) ? modelInfo : null;
                bool isCapture = modelId is >= 14000 and <= 14999;
                // Captures all share a handful of generic catalog IDs (e.g. 14000), so the
                // catalog name is just a placeholder ("Eltron 30") — the real user-given name
                // travels as a string param instead (content-hash-prefixed file identifier).
                string name = (isCapture ? ExtractCaptureName(model) : null) ?? info?.Name ?? $"#{modelId}";
                string category = info?.Category ?? string.Empty;
                bool bypassed = ResolveBypassState(sceneBypasses, rowIndex, slotIndex, currentScene);
                List<BlockParam> parameters = BuildBlockParams(model, modelId, currentScene);

                blocks.Add(new Block
                {
                    ModelId = modelId,
                    Name = name,
                    Category = category,
                    Bypassed = bypassed,
                    SlotIndex = slotIndex,
                    IsCapture = isCapture,
                    Params = parameters
                });
            }

            blocks = [.. blocks.OrderBy(b => b.SlotIndex)];

            // Build SplitInfo from splitter/mixer models + SplitControlPoints.
            // Every even row carries a splitter/mixer/split_control_points entry
            // whether or not a branch is active — a dormant one reports split=-1
            // AND mix=-1 (per pyquadcortex's `splits()`: "A branch is present when
            // split >= 0 ... A row with no branch reports -1 for both and is
            // omitted") — so skip only that fully-dormant case. A branch whose
            // paired row starts from its own input rather than forking mid-chain
            // (e.g. two rows both fed from "Input 1") reports split=-1 with a
            // real, non-negative mix column — that's still an active split, just
            // with no local fork point, and must still produce a SplitInfo or
            // the paired row's rejoin never renders.
            List<SplitInfo> splits = new();
            foreach (SplitControlPoints? scp in chain.SplitControlPoints)
            {
                if (scp.Split < 0 && scp.Mix < 0) continue;

                int splitCol = scp.Split;
                int mixCol = scp.Mix;

                Model? splitterModel = chain.Splitter.FirstOrDefault(m => m.HasColumn && (int)m.Column == splitCol);
                Model? mixerModel = chain.Mixer.FirstOrDefault(m => m.HasColumn && (int)m.Column == mixCol);

                int splitterModelId = splitterModel?.HasHash == true ? (int)splitterModel.Hash : 0;
                int mixerModelId = mixerModel?.HasHash == true ? (int)mixerModel.Hash : 0;

                string splitterName = "Split";
                string mixerName = "Merge";
                if (splitterModelId > 0 && _modelMap.TryGetValue(splitterModelId, out ModelInfo? smi))
                    splitterName = smi.Name;
                if (mixerModelId > 0 && _modelMap.TryGetValue(mixerModelId, out ModelInfo? mmi))
                    mixerName = mmi.Name;

                List<BlockParam> splitterParams = splitterModel != null
                    ? BuildBlockParams(splitterModel, splitterModelId, currentScene)
                    : [];
                List<BlockParam> mixerParams = mixerModel != null
                    ? BuildBlockParams(mixerModel, mixerModelId, currentScene)
                    : [];

                splits.Add(new SplitInfo
                {
                    SplitterSlotIndex = splitCol,
                    MixerSlotIndex = mixCol,
                    SplitterModelId = splitterModelId,
                    MixerModelId = mixerModelId,
                    SplitterName = splitterName,
                    MixerName = mixerName,
                    SplitterParams = splitterParams,
                    MixerParams = mixerParams
                });
            }

            return new ChainBlocksResult(blocks, splits);
        }

        private static bool ResolveBypassState(
            Dictionary<int, Dictionary<int, Dictionary<int, bool>>> sceneBypasses,
            int rowIndex, int slotIndex, int currentScene)
        {
            return sceneBypasses.TryGetValue(rowIndex, out Dictionary<int, Dictionary<int, bool>>? colMap) &&
                   colMap.TryGetValue(slotIndex, out Dictionary<int, bool>? sceneMap) &&
                   sceneMap.TryGetValue(currentScene, out bool value) && value;
        }

        private static ChainIO BuildChainIo(Chain chain)
        {
            int inPortId = chain.HasInPortid ? (int)chain.InPortid : -1;
            int outPortId = chain.HasOutPortid ? (int)chain.OutPortid : -1;

            string input = inPortId >= 0 && InputPortNames.TryGetValue(inPortId, out string? inName) ? inName : string.Empty;
            string output = outPortId >= 0 && OutputPortNames.TryGetValue(outPortId, out string? outName) ? outName : string.Empty;

            return new ChainIO(input, output, inPortId, outPortId);
        }

        private static Dictionary<int, Dictionary<int, Dictionary<int, bool>>> BuildSceneBypassMap(BinaryPreset preset)
        {
            Dictionary<int, Dictionary<int, Dictionary<int, bool>>> result = new();

            for (int bypassPos = 0; bypassPos < preset.Bypass.Count; bypassPos++)
            {
                Bypass bypass = preset.Bypass[bypassPos];
                // Row/Column are frequently absent on a full preset dump — array
                // position is the fallback, matching the echo-confirmation convention.
                int row = bypass.HasRow ? (int)bypass.Row : bypassPos;
                if (!result.TryGetValue(row, out Dictionary<int, Dictionary<int, bool>>? colMap))
                {
                    colMap = [];
                    result[row] = colMap;
                }

                for (int colPos = 0; colPos < bypass.ColBypass.Count; colPos++)
                {
                    ColBypass col = bypass.ColBypass[colPos];
                    int colIndex = col.HasColumn ? (int)col.Column : colPos;
                    Dictionary<int, bool> sceneMap = new();

                    // A block without sceneMode carries a global bypass state that
                    // applies regardless of the active scene (the device writes it
                    // across all 8 internal scene slots — pyquadcortex-documented);
                    // key it under every scene rather than only the entry's own index.
                    bool sceneSpecific = col.HasSceneMode && col.SceneMode;
                    if (sceneSpecific)
                    {
                        for (int i = 0; i < col.SceneBypass.Count; i++)
                        {
                            sceneMap[i] = col.SceneBypass[i].Bypass;
                        }
                    }
                    else if (col.SceneBypass.Count > 0)
                    {
                        bool globalValue = col.SceneBypass[0].Bypass;
                        for (int scene = 0; scene < 8; scene++)
                        {
                            sceneMap[scene] = globalValue;
                        }
                    }

                    colMap[colIndex] = sceneMap;
                }
            }

            return result;
        }

        // A Capture's "file_name" param carries the display name as a string value,
        // prefixed with a 64-char hex content hash and no separator
        // (e.g. "67f310ac...656aafe9Chief Bass Overdrive 1"). Scan all params/scenes for
        // it since the device sometimes sends a second, empty numeric entry at the same index.
        private static string? ExtractCaptureName(Model model)
        {
            const int HashPrefixLength = 64;

            foreach (Param? param in model.Params)
            {
                foreach (ParamValue paramValue in param.ParamValues)
                {
                    if (paramValue.ValueCase != ParamValue.ValueOneofCase.StringValue) continue;

                    string raw = paramValue.StringValue;
                    if (string.IsNullOrEmpty(raw)) continue;

                    string name = raw.Length > HashPrefixLength && raw[..HashPrefixLength].All(Uri.IsHexDigit)
                        ? raw[HashPrefixLength..]
                        : raw;

                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }

            return null;
        }

        private List<BlockParam> BuildBlockParams(Model model, int modelId, int currentScene)
        {
            List<BlockParam> parameters = new();
            List<ParamDef>? defs = _modelMap.TryGetValue(modelId, out ModelInfo? modelInfo) ? modelInfo.ParamDefs : null;

            foreach (Param? param in model.Params)
            {
                if (param.ParamValues.Count == 0)
                {
                    continue;
                }

                int index = param.HasIndex ? (int)param.Index : parameters.Count;
                ParamDef? def = defs != null && index < defs.Count ? defs[index] : null;
                ParamValue valueEntry = param.ParamValues.Count > currentScene
                    ? param.ParamValues[currentScene]
                    : param.ParamValues[0];

                if (!TryGetParamValue(valueEntry, out float value))
                {
                    continue;
                }

                float min = def?.Min ?? (param.HasExpressionMin ? param.ExpressionMin : 0f);
                float max = def?.Max ?? (param.HasExpressionMax ? param.ExpressionMax : 1f);
                ParamType paramType = def?.ParamType ?? ParamType.Unknown;

                parameters.Add(new BlockParam
                {
                    Index = index,
                    Name = def?.Name ?? $"Param {index + 1}",
                    Value = value,
                    Min = min,
                    Max = max,
                    ParamType = paramType
                });
            }

            return parameters;
        }

        private static bool TryGetParamValue(ParamValue valueEntry, out float value)
        {
            switch (valueEntry.ValueCase)
            {
                case ParamValue.ValueOneofCase.FloatValue:
                    value = valueEntry.FloatValue;
                    return true;
                case ParamValue.ValueOneofCase.IntValue:
                    value = valueEntry.IntValue;
                    return true;
                default:
                    value = 0f;
                    return false;
            }
        }

        private async Task RequestPresetLibraryAsync(TimeSpan maxDuration, TimeSpan idleTimeout, bool skipLock = false)
        {
            if (!_isConnected)
            {
                return;
            }

            if (!skipLock)
            {
                await _operationSemaphore.WaitAsync(_cts.Token);
            }
            try
            {
                byte[] query = ProtobufBuilder.BuildStateQuery();
                if (!SendCommand(query, MessageTypes.File))
                {
                    return;
                }

                DateTime deadline = DateTime.UtcNow + maxDuration;
                while (DateTime.UtcNow < deadline)
                {
                    WirePayload? response = _client.WaitForMessage(MessageTypes.File, idleTimeout);
                    if (response == null)
                    {
                        break;
                    }

                    _fileMessages.Add(response.Payload);
                }

                RebuildPresetLibrary();
            }
            finally
            {
                if (!skipLock)
                {
                    _operationSemaphore.Release();
                }
            }
        }

        private void RebuildPresetLibrary()
        {
            _logger.LogDebug("[ProtocolService] Rebuilding preset library from {FileMessageCount} file messages...", _fileMessages.Count);
            List<PresetDirectory> flatDirs = new();

            foreach (byte[] payload in _fileMessages)
            {
                ParsedFolder? folder = ParseFolderInfo(payload);
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    continue;
                }

                string path = folder.Path;
                if (!path.StartsWith("/", StringComparison.Ordinal) ||
                    path.StartsWith("/opt/neuraldsp/impulse_responses", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(folder.Name)
                    ? path.Substring(path.LastIndexOf('/') + 1)
                    : folder.Name;

                flatDirs.Add(new PresetDirectory
                {
                    Name = name,
                    Path = path,
                    Presets = folder.Presets
                });
            }

            List<PresetDirectory> presetLibrary = BuildDirectoryTree(flatDirs);
            _logger.LogDebug("[ProtocolService] Preset library rebuilt: {FolderCount} folders, {RootCount} roots, library={ItemCount} items",
                flatDirs.Count, presetLibrary.Count, _currentState.PresetLibrary.Count);
            _currentState = _currentState with { PresetLibrary = presetLibrary, Timestamp = DateTime.UtcNow };
            _lastLibraryRebuild = DateTime.UtcNow;
        }

        private ParsedFolder? ParseFolderInfo(byte[] payload)
        {
            try
            {
                byte[] data = CompressionUtils.DecompressIfNeeded(payload);
                FileMessage message = FileMessage.Parser.ParseFrom(data);
                if (message.Folder == null)
                {
                    return null;
                }

                FolderInfo folder = message.Folder;
                List<PresetEntry> presets = new();
                foreach (ProductData? file in folder.Files)
                {
                    presets.Add(new PresetEntry
                    {
                        Path = file.Key ?? string.Empty,
                        Index = file.Index,
                        Name = file.Name ?? string.Empty,
                        Author = file.Author ?? string.Empty,
                        Uuid = file.AuthorId ?? string.Empty
                    });
                }

                return new ParsedFolder(folder.Key ?? string.Empty, folder.Name ?? string.Empty, presets);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing FileMessage");
                return null;
            }
        }

        private static List<PresetDirectory> BuildDirectoryTree(List<PresetDirectory> flatDirs)
        {
            List<NamedRoot> knownRoots = new()
            {
                new NamedRoot("/media/p4/Presets", "Presets"),
                new NamedRoot("/opt/neuraldsp/Factory Library", "Factory Library"),
                new NamedRoot("/opt/neuraldsp/Plugins", "Plugins")
            };

            Dictionary<string, List<PresetDirectory>> groups = new();
            Dictionary<string, string> rootNames = new();

            foreach (PresetDirectory dir in flatDirs)
            {
                NamedRoot? root = knownRoots.FirstOrDefault(rootEntry =>
                    dir.Path.Equals(rootEntry.Path, StringComparison.Ordinal) ||
                    dir.Path.StartsWith(rootEntry.Path + "/", StringComparison.Ordinal));

                if (root == null || string.IsNullOrEmpty(root.Path))
                {
                    continue;
                }

                if (!groups.TryGetValue(root.Path, out List<PresetDirectory>? list))
                {
                    list = [];
                    groups[root.Path] = list;
                    rootNames[root.Path] = root.Name;
                }

                list.Add(dir);
            }

            List<PresetDirectory> DirectChildren(string parentPath, List<PresetDirectory> dirs)
            {
                return dirs
                    .Where(dir => dir.Path != parentPath && dir.Path.StartsWith(parentPath + "/", StringComparison.Ordinal))
                    .Where(dir => !dirs.Any(mid =>
                        mid.Path != parentPath &&
                        mid.Path != dir.Path &&
                        dir.Path.StartsWith(mid.Path + "/", StringComparison.Ordinal) &&
                        mid.Path.StartsWith(parentPath + "/", StringComparison.Ordinal)))
                    .Select(dir => dir with { Children = DirectChildren(dir.Path, dirs) })
                    .OrderBy(dir => dir.Name)
                    .ToList();
            }

            return groups.Select(group =>
            {
                string rootPath = group.Key;
                List<PresetDirectory> dirs = group.Value;
                PresetDirectory? rootDir = dirs.FirstOrDefault(dir => dir.Path == rootPath);
                List<PresetDirectory> children = DirectChildren(rootPath, dirs);

                if (rootDir != null)
                {
                    return rootDir with
                    {
                        Name = rootNames[rootPath],
                        Children = children
                    };
                }

                return new PresetDirectory
                {
                    Name = rootNames[rootPath],
                    Path = rootPath,
                    Children = children
                };
            }).OrderBy(dir => dir.Name).ToList();
        }

        /// <summary>
        /// Parses scene index from protobuf payload.
        /// Expected format: {f1=1, f3=scene_index} or {f3=scene_index}
        /// </summary>
        private int ParseSceneMessage(byte[] payload)
        {
            try
            {
                _logger.LogDebug("[ProtocolService] Parsing scene from payload: {Payload} ({ByteCount} bytes)", Convert.ToHexString(payload), payload.Length);

                // Simple parser: look for field 3 (varint)
                // Field 3 tag = (3 << 3) | 0 = 24 (0x18)
                for (int i = 0; i < payload.Length - 1; i++)
                {
                    if (payload[i] == 0x18) // Field 3, varint
                    {
                        // Next byte is the scene index (assuming < 128)
                        int sceneValue = payload[i + 1];
                        _logger.LogDebug("[ProtocolService] Found scene field at offset {Offset}: value = {SceneValue}", i, sceneValue);
                        return sceneValue;
                    }
                }

                _logger.LogWarning("[ProtocolService] No scene field (0x18) found in payload");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing scene");
            }
            return -1;
        }

        /// <summary>
        /// Parses mode from protobuf payload.
        /// Expected format: {f1=1, f3=mode} or {f3=mode}
        /// </summary>
        private int ParseModeMessage(byte[] payload)
        {
            try
            {
                // Simple parser: look for field 3 (varint)
                // Field 3 tag = (3 << 3) | 0 = 24 (0x18)
                for (int i = 0; i < payload.Length - 1; i++)
                {
                    if (payload[i] == 0x18) // Field 3, varint
                    {
                        return payload[i + 1];
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing mode");
            }
            return -1;
        }

        private static int ExtractBpmFromPreset(BinaryPreset preset, int scene)
        {
            if (scene >= 0 && scene < preset.SceneTempo.Count)
            {
                return (int)preset.SceneTempo[scene];
            }
            return 0;
        }

        /// <summary>
        /// Parses BPM from a protobuf GlobalTempoMessage payload.
        /// The device reports tempo either as a raw int or as the normalized
        /// float (bpm = 40 + 200 * float). Metronome heartbeats carry
        /// MetronomeStatus.CurrentBeat (1-3), not BPM.
        /// </summary>
        private int ParseTempoMessage(byte[] payload)
        {
            try
            {
                GlobalTempoMessage msg = GlobalTempoMessage.Parser.ParseFrom(payload);

                // If this is a metronome status update (has MetronomeStatus), it's not BPM
                if (msg.MetronomeStatus != null)
                {
                    return -1;
                }

                // Extract BPM from Params[0].ParamValues[0]
                if (msg.Params.Count > 0 && msg.Params[0].ParamValues.Count > 0)
                {
                    ParamValue value = msg.Params[0].ParamValues[0];
                    if (value.HasFloatValue)
                    {
                        // Tempo wire value is normalized over the 40..240 span.
                        return (int)Math.Round(40.0 + 200.0 * value.FloatValue);
                    }
                    return value.IntValue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error parsing tempo message");
            }
            return -1;
        }

        /// <summary>
        /// Fires the state changed event on a background thread.
        /// </summary>
        private void FireStateChanged(StateUpdate update)
        {
            // Skip events during initialization to prevent spam
            if (_suppressStateEvents)
            {
                _logger.LogDebug("[ProtocolService] Suppressing state event during init: {ChangeType}", update.ChangeType);
                return;
            }

            _logger.LogDebug("[ProtocolService] Firing state change event: {ChangeType}, Scene={Scene}, BPM={Bpm}", update.ChangeType, update.State.Scene, update.State.Bpm);

            try
            {
                OnStateChanged?.Invoke(update);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Error firing state changed event");
            }
        }

        /// <summary>
        /// Minimal polling for scene changes since the device doesn't push scene updates automatically.
        /// Only checks scene every 3 seconds to avoid spam.
        /// </summary>
        private async void PollHardwareState(object? state)
        {
            if (!_isConnected || _suppressStateEvents)
                return;

            try
            {
                // Only query scene changes - the most common hardware interaction
                _logger.LogDebug("[ProtocolService] Checking for scene changes...");

                int oldScene = _currentState.Scene;
                await QueryStateFieldAsync(MessageTypes.Scene, "Scene", TimeSpan.FromSeconds(2));

                // The QueryStateFieldAsync call will trigger ProcessIncomingMessage if scene changed
                // We don't need to manually fire events here since ProcessIncomingMessage handles it

                if (_currentState.Scene != oldScene)
                {
                    _logger.LogDebug("[ProtocolService] Scene change confirmed: {OldScene} -> {NewScene}", oldScene, _currentState.Scene);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolService] Scene polling error");
            }
        }

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
                _isConnected = false;

                _logger.LogDebug("[ProtocolService] Disposing...");

                _statePollingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _statePollingTimer?.Dispose();

                _reconnectWatchdogTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _reconnectWatchdogTimer.Dispose();

                try
                {
                    _cts.Cancel();
                    _messageProcessorThread?.Join(1000);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ProtocolService] Error stopping message processor");
                }

                _cts.Dispose();
                _operationSemaphore.Dispose();
                _client?.Dispose();
            }
        }

        private volatile bool _disposed;
    }
}
