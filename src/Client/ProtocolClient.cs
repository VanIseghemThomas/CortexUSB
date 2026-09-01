using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenCortex.CortexUSB.Models;
using OpenCortex.CortexUSB.Protocol;
using CortexProtobufV2;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace OpenCortex.CortexUSB.Client
{
    /// <summary>
    /// High-level client that exposes domain actions using ITransport.
    /// For now it supports Connect (handshake), ListPresets, GetCurrentPreset, RecallPreset.
    /// </summary>
    public class ProtocolClient : IDisposable
    {
        private readonly ITransport _transport;
        private readonly ChunkAssembler _assembler = new();
        private readonly WireParser _parser = new();
        private readonly ILogger<ProtocolClient> _logger;
        private readonly Queue<WirePayload> _received = new();
        private bool _connected;
        private byte[]? _aesKey;
        private byte[]? _aesIv;
        private CancellationTokenSource? _cts;
        private Thread? _readerThread;
        private readonly ConcurrentDictionary<uint, ConcurrentQueue<WirePayload>> _byType = new();
        public Action<WirePayload>? OnMessageReceived { get; set; }
        /// <summary>
        /// Raised when the reader loop detects the connection is dead (USB stall — no
        /// data for <see cref="StallTimeoutMs"/>). Fired from the reader thread itself,
        /// right before it exits; the transport has already been torn down via
        /// <see cref="ITransport.Close"/> so a subsequent <see cref="Connect"/> can
        /// cleanly re-open it.
        /// </summary>
        public event Action<string>? ConnectionLost;
        private readonly object _sendLock = new();
        private volatile BinaryPreset? _lastRecallPreset;
        // Diagnostics: last-seen timestamps and message counts per message type
        private readonly ConcurrentDictionary<uint, DateTime> _lastMessageAt = new();
        private readonly ConcurrentDictionary<uint, long> _messageCounts = new();
        private long _lastModelRepoSize = 0;
        private DateTime? _lastModelRepoAt = null;
        private readonly int _idleMsBeforeAction;
        private readonly int _chunkDelayMs;
        private readonly bool _fetchModelRepoInHandshake;
        private Timer? _keepAliveTimer;
        private volatile bool _disposed;

        public bool IsConnected => _connected && !_disposed;

        public ProtocolClient(ITransport transport, int idleMsBeforeAction = 1000, Microsoft.Extensions.Logging.ILogger<ProtocolClient>? logger = null, bool fetchModelRepoInHandshake = true)
        {
            _transport = transport;
            // Instant unplug notification — fires on the transport's own device-watcher
            // thread, independent of the reader thread's much slower (~15s) stall check.
            _transport.DeviceRemoved += () => HandleConnectionLost("USB device physically removed");
            // allow override from constructor param and environment variable for quick experiments
            _idleMsBeforeAction = idleMsBeforeAction;
            string? env = Environment.GetEnvironmentVariable("CORTEX_IDLE_MS");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out int ev))
            {
                _idleMsBeforeAction = ev;
            }
            string? delayEnv = Environment.GetEnvironmentVariable("CORTEX_CHUNK_DELAY_MS");
            if (!string.IsNullOrWhiteSpace(delayEnv) && int.TryParse(delayEnv, out int d))
            {
                _chunkDelayMs = Math.Max(0, d);
            }
            if (logger != null)
            {
                _logger = logger;
            }
            else
            {
                // Fallback to a very small console logger to avoid pulling extra packages in tests
                _logger = new SimpleConsoleLogger<ProtocolClient>();
            }

            _fetchModelRepoInHandshake = fetchModelRepoInHandshake;

            _logger.LogInformation("[ProtocolClient] idleMsBeforeAction={Idle}ms (constructor={Ctor}, env={Env}), chunkDelay={ChunkDelay}ms (env={DelayEnv})",
                _idleMsBeforeAction, idleMsBeforeAction, env, _chunkDelayMs, delayEnv);
        }

        private sealed record DerivedKey(byte[] Key, byte[] Iv);

        private static DerivedKey DeriveKeyAndIv(byte[] passphrase, int count)
        {
            using SHA1 sha1 = SHA1.Create();
            List<byte> derived = [];
            byte[] prev = [];
            while (derived.Count < 28)
            {
                byte[] data = new byte[prev.Length + passphrase.Length];
                Array.Copy(prev, 0, data, 0, prev.Length);
                Array.Copy(passphrase, 0, data, prev.Length, passphrase.Length);
                byte[] d = data;
                for (int i = 0; i < count; i++) d = sha1.ComputeHash(d);
                derived.AddRange(d);
                prev = d;
            }
            byte[] key = derived.GetRange(0, 16).ToArray();
            byte[] iv = derived.GetRange(16, 12).ToArray();
            return new DerivedKey(key, iv);
        }

        public bool Connect(TimeSpan timeout)
        {
            if (_connected) return true;

            if (!_transport.Open()) return false;

            _connected = false;
            _lastRecallPreset = null;
            _aesKey = null;
            _aesIv = null;
            _byType.Clear();

            string sessionId = Guid.NewGuid().ToString("N");
            SendMessageSafe(ProtocolMessages.BuildResetComms(sessionId), 52, "Send ResetComms failed");
            Thread.Sleep(200);
            SendMessageSafe(ProtocolMessages.BuildVersionRequest(), 10, "Send VersionReq failed");

            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                byte[]? report = _transport.Read(200);
                if (report == null) continue;
                byte[]? complete = _assembler.ProcessChunk(report);
                if (complete == null) continue;

                WirePayload w = _parser.Parse(complete);
                EnqueueByType(w);
                if (w.MessageType != 10) continue;

                PerformHandshakeSteps(w);
                StartBackgroundReader();
                WaitForModelRepoIfNeeded();
                StartKeepAlive();

                _connected = true;
                return true;
            }

            return false;
        }

        private void PerformHandshakeSteps(WirePayload versionResponse)
        {
            ExtractKeyMaterial(versionResponse);

            SendMessageSafe(ProtocolMessages.BuildVersionReply(), 10, "Send VersionReply failed");
            Thread.Sleep(200);
            SendMessageSafe(ProtocolMessages.BuildConnection(), 49, "Send Connection failed");
            Thread.Sleep(200);
            SendMessageSafe(ProtocolMessages.BuildModelRepoRequest(), 51, "Send ModelRepo request failed");
        }

        private void ExtractKeyMaterial(WirePayload versionResponse)
        {
            try
            {
                VersionMessage ver = VersionMessage.Parser.ParseFrom(versionResponse.Payload);
                if (ver.CommsVersion == null || ver.CommsVersion.Length == 0) return;

                byte[] encryptedKeyMaterial = ver.CommsVersion.ToByteArray();
                uint[] keyWords = [0x83d20984u, 0xb36021beu, 0x9c2263bcu, 0x4bb22d16u];
                byte[] xxteaKey = new byte[16];
                for (int i = 0; i < 4; i++)
                    Array.Copy(BitConverter.GetBytes(keyWords[i]), 0, xxteaKey, i * 4, 4);

                byte[] decrypted = Encryption.XXTEA.Decrypt(encryptedKeyMaterial, xxteaKey);
                DerivedKey dk = DeriveKeyAndIv(decrypted, 10);
                _aesKey = dk.Key;
                _aesIv = dk.Iv;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProtocolClient] Extract key_material failed: {ex.Message}");
            }
        }

        private void SendMessageSafe(byte[] payload, uint messageType, string errorMessage)
        {
            try { SendWireMessage(payload, messageType); }
            catch (Exception ex) { Console.WriteLine($"[ProtocolClient] {errorMessage}: {ex.Message}"); }
        }

        private void StartBackgroundReader()
        {
            _cts = new CancellationTokenSource();
            _readerThread = new Thread(() => ReaderLoop(_cts.Token)) { IsBackground = true };
            _readerThread.Start();
        }

        private void WaitForModelRepoIfNeeded()
        {
            if (!_fetchModelRepoInHandshake) return;

            Console.WriteLine("[ProtocolClient] Waiting for ModelRepo response...");
            WirePayload? modelRepo = WaitForMessage(51, TimeSpan.FromSeconds(15));
            if (modelRepo != null)
            {
                _lastModelRepoSize = modelRepo.Payload.Length;
                _lastModelRepoAt = DateTime.UtcNow;
                Console.WriteLine($"[ProtocolClient] ✅ ModelRepo received: {modelRepo.Payload.Length} bytes");
            }
            else
            {
                Console.WriteLine("[ProtocolClient] ⚠️ ModelRepo not received within timeout, continuing anyway");
            }
        }

        private void StartKeepAlive()
        {
            _keepAliveTimer = new Timer(SendKeepAlive, null, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800));
            Console.WriteLine("[ProtocolClient] ✅ KeepAlive started (800ms interval)");
        }

        private const int StallTimeoutMs = 15_000;

        private void ReaderLoop(CancellationToken ct)
        {
            DateTime lastReportAt = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    byte[]? report = _transport.Read(200);
                    if (report == null)
                    {
                        // No report this cycle — check for USB stall
                        if (_connected && (DateTime.UtcNow - lastReportAt).TotalMilliseconds > StallTimeoutMs)
                        {
                            Console.WriteLine($"[ProtocolClient] ❌ USB stall detected — no data for {StallTimeoutMs / 1000}s, marking disconnected");
                            HandleConnectionLost("USB stall — no data received for " + StallTimeoutMs / 1000 + "s");
                            return;
                        }
                        continue;
                    }

                    lastReportAt = DateTime.UtcNow;
                    byte[]? complete = _assembler.ProcessChunk(report);
                    if (complete == null) continue;
                    WirePayload w = _parser.Parse(complete, _aesKey, _aesIv);
                    ProcessReaderMessage(w);
                    EnqueueByType(w);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ProtocolClient] ReaderLoop error: " + ex.Message);
                    Thread.Sleep(20);
                }
            }
        }

        private void ProcessReaderMessage(WirePayload w)
        {
            if (w.MessageType == 15)
            {
                TryCacheRecallPreset(w);
            }
            if (w.MessageType == 33)
            {
                TryParseGlobalTempo(w);
            }
        }

        private void TryCacheRecallPreset(WirePayload w)
        {
            try
            {
                RecallPresetMessage rp = RecallPresetMessage.Parser.ParseFrom(w.Payload);
                _lastRecallPreset = rp.Preset;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[ProtocolClient] Parse RecallPreset failed"); }
        }

        private static void TryParseGlobalTempo(WirePayload w)
        {
            try
            {
                _ = GlobalTempoMessage.Parser.ParseFrom(w.Payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ProtocolClient] Parse GlobalTempo failed: " + ex.Message);
            }
        }

        private void EnqueueByType(WirePayload w)
        {
            // Log incoming message for integration debugging
            try
            {
                string prefix = BitConverter.ToString(w.Payload.Length > 16 ? w.Payload[..16] : w.Payload).Replace("-", " ");
                _logger.LogDebug("[ProtocolClient] Rx message type={Type}, len={Len}, head={Head}", w.MessageType, w.Payload.Length, prefix);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[ProtocolClient] Failed to log Rx message head"); }

            ConcurrentQueue<WirePayload> q = _byType.GetOrAdd(w.MessageType, _ => new ConcurrentQueue<WirePayload>());
            q.Enqueue(w);

            // Cap each per-type queue at 100 entries to bound memory growth.
            // Stale messages accumulate when no WaitForMessage consumer is actively draining them.
            if (q.Count > 100)
            {
                while (q.Count > 50 && q.TryDequeue(out _))
                {
                    // Discard oldest messages to keep queue size reasonable
                }
            }

            // Diagnostics
            _lastMessageAt[w.MessageType] = DateTime.UtcNow;
            _messageCounts.AddOrUpdate(w.MessageType, 1, (_, v) => v + 1);
            OnMessageReceived?.Invoke(w);
        }

        // Wait until no messages of the given types have arrived for the specified idle period
        public bool WaitForIdle(TimeSpan idle, TimeSpan maxWait, params uint[] messageTypes)
        {
            DateTime deadline = DateTime.UtcNow + maxWait;
            while (DateTime.UtcNow < deadline)
            {
                bool allIdle = true;
                DateTime now = DateTime.UtcNow;
                foreach (uint t in messageTypes)
                {
                    if (_lastMessageAt.TryGetValue(t, out DateTime last) && (now - last) < idle)
                    {
                        allIdle = false;
                        break;
                    }
                    // if we never saw this type, treat as idle for this check
                }
                if (allIdle) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        public WirePayload? WaitForMessage(uint messageType, TimeSpan timeout)
            => WaitForMessage(messageType, _ => true, timeout);

        /// <summary>
        /// Waits for the next message of <paramref name="messageType"/> satisfying
        /// <paramref name="match"/>. Non-matching messages of that type are consumed
        /// and discarded — the message processor path is separate (it feeds through
        /// <see cref="OnMessageReceived"/>), so nothing stateful is lost.
        /// Callers must serialize access (the service holds its operation semaphore).
        /// </summary>
        public WirePayload? WaitForMessage(uint messageType, Func<WirePayload, bool> match, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_byType.TryGetValue(messageType, out ConcurrentQueue<WirePayload>? q))
                {
                    while (q.TryDequeue(out WirePayload? v))
                    {
                        if (match(v)) return v;
                    }
                }
                Thread.Sleep(10);
            }
            return null;
        }

        public void SendWireMessage(byte[] protobufPayload, uint messageType, bool encrypt = false, bool compressed = false)
        {
            byte[] wire = ProtocolMessages.EncodeWire(protobufPayload, messageType, encrypt, compressed);

            try
            {
                string head = BitConverter.ToString(wire.Length > 16 ? wire[..16] : wire).Replace("-", " ");
                _logger.LogDebug("[ProtocolClient] Tx message type={Type}, wireLen={Len}, head={Head}", messageType, wire.Length, head);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[ProtocolClient] Failed to log Tx message head"); }

            // Chunk and send
            lock (_sendLock)
            {
                if (wire.Length > 126)
                {
                    int totalChunks = (wire.Length + 125) / 126;
                    int offset = 0;
                    for (int i = 0; i < totalChunks; i++)
                    {
                        int chunkPayload = Math.Min(126, wire.Length - offset);
                        bool isFirst = i == 0;
                        bool isLast = i == totalChunks - 1;
                        ushort header = (ushort)((isFirst ? 0x4000 : 0) | (isLast ? 0x8000 : 0) | chunkPayload);
                        byte[] chunk = new byte[129];
                        chunk[0] = 0x02; // Output Report
                        chunk[1] = (byte)(header & 0xFF);
                        chunk[2] = (byte)((header >> 8) & 0xFF);
                        Array.Copy(wire, offset, chunk, 3, chunkPayload);
                        if (!_transport.Write(chunk)) throw new IOException("Write failed");
                        // Optional inter-chunk delay for pacing experiments
                        if (_chunkDelayMs > 0) Thread.Sleep(_chunkDelayMs);
                        offset += chunkPayload;
                    }
                }
                else
                {
                    byte[] chunk = new byte[129];
                    chunk[0] = 0x02;
                    ushort header = (ushort)(0xC000 | wire.Length);
                    chunk[1] = (byte)(header & 0xFF);
                    chunk[2] = (byte)((header >> 8) & 0xFF);
                    Array.Copy(wire, 0, chunk, 3, wire.Length);
                    if (!_transport.Write(chunk)) throw new IOException("Write failed");
                    if (_chunkDelayMs > 0) Thread.Sleep(_chunkDelayMs);
                }
            }
        }

        public IList<ProductData> GetLoadedPresets(TimeSpan timeout)
        {
            // Send File listing request: FileMessage { action = READ }
            FileMessage fm = new() { Action = MessageAction.Types.Enum.Read };
            byte[] payload = fm.ToByteArray();
            SendWireMessage(payload, 4);

            // Collect responses for the timeout period
            List<ProductData> list = [];
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                WirePayload? w = WaitForMessage(4, TimeSpan.FromMilliseconds(200));
                if (w == null) continue;
                try
                {
                    FileMessage fmResp = FileMessage.Parser.ParseFrom(w.Payload);
                    if (fmResp.Folder != null)
                    {
                        foreach (ProductData? f in fmResp.Folder.Files)
                        {
                            list.Add(f);
                        }

                        if (fmResp.Folder.Files.Any(f => f.Name == "integration-test"))
                            Console.WriteLine("[ProtocolClient] Found integration-test in file list");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProtocolClient] Failed to parse FileMessage: {ex.Message}");
                }
            }
            // After collecting file messages, wait for an idle window to ensure large transfers finished
            if (!WaitForIdle(TimeSpan.FromMilliseconds(_idleMsBeforeAction), TimeSpan.FromSeconds(5), 4, 51))
            {
                Console.WriteLine("[ProtocolClient] Warning: Did not reach idle after file list within 5s");
            }
            return list;
        }

        public byte[]? GetModelRepo(TimeSpan timeout)
        {
            // Request ModelRepo (type 51)
            byte[] req = ProtocolMessages.BuildModelRepoRequest();
            SendWireMessage(req, 51);
            WirePayload? w = WaitForMessage(51, timeout);
            if (w != null)
            {
                _lastModelRepoSize = w.Payload?.Length ?? 0;
                _lastModelRepoAt = DateTime.UtcNow;
                // Wait for any streaming FileMessage chunks to finish
                if (!WaitForIdle(TimeSpan.FromMilliseconds(_idleMsBeforeAction), TimeSpan.FromSeconds(10), 4))
                {
                    Console.WriteLine("[ProtocolClient] Warning: ModelRepo may still be streaming file messages (idle timeout)");
                }
            }
            return w?.Payload;
        }

        public BinaryPreset? GetCurrentPreset(TimeSpan timeout)
        {
            // Prefer cached last RecallPreset if available
            if (_lastRecallPreset != null) return _lastRecallPreset;

            // Proactively query device for both setlist position (type 2) and recall preset (type 15).
            // Some devices respond to the position query before emitting the full preset; sending
            // both increases the chance we get a RecallPreset message in response.
            try
            {
                byte[] stateQuery = ProtobufBuilder.BuildStateQuery();
                try { SendWireMessage(stateQuery, 2); } catch (Exception ex) { _logger.LogWarning(ex, "[ProtocolClient] Failed to send SetlistPosition query"); }
                Thread.Sleep(50);
                try { SendWireMessage(stateQuery, 15); } catch (Exception ex) { _logger.LogWarning(ex, "[ProtocolClient] Failed to send RecallPreset query"); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolClient] Failed to send state queries");
            }

            WirePayload? w = WaitForMessage(15, timeout);
            if (w == null) return null;
            try
            {
                RecallPresetMessage rp = RecallPresetMessage.Parser.ParseFrom(w.Payload);
                _lastRecallPreset = rp.Preset;
                return rp.Preset;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProtocolClient] GetCurrentPreset parse failed");
                return null;
            }
        }

        public bool RecallPresetByName(string name, TimeSpan timeout)
        {
            if (!WaitForIdle(TimeSpan.FromMilliseconds(_idleMsBeforeAction), TimeSpan.FromSeconds(5), 4, 51))
            {
                Console.WriteLine("[ProtocolClient] Warning: Not idle before RecallPreset - proceeding anyway");
            }

            SendMessageSafe(new FileMessage { Action = MessageAction.Types.Enum.Read }.ToByteArray(), 4, "Failed to send File listing request");

            PresetLocation loc = FindPresetInFileListings(name, timeout);
            if (loc.FolderKey == null || loc.PresetIndex < 0)
            {
                Console.WriteLine($"[ProtocolClient] Could not find preset '{name}' in file listings");
                return false;
            }

            return SendSetlistPositionAndVerifyRecall(loc.FolderKey, loc.PresetIndex, loc.IsFactory, name, timeout);
        }

        /// <summary>
        /// Queries the device file listing and finds the folder key and index for a given preset path.
        /// </summary>
        public PresetLocation FindPresetByPathFromDevice(string presetPath, TimeSpan timeout)
        {
            if (!WaitForIdle(TimeSpan.FromMilliseconds(_idleMsBeforeAction), TimeSpan.FromSeconds(5), 4, 51))
            {
                Console.WriteLine("[ProtocolClient] Warning: Not idle before FindPresetByPath - proceeding anyway");
            }

            SendMessageSafe(new FileMessage { Action = MessageAction.Types.Enum.Read }.ToByteArray(), 4, "Failed to send File listing request");

            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                WirePayload? w = WaitForMessage(4, TimeSpan.FromMilliseconds(200));
                if (w == null) continue;

                FileMessage? fmResp = TryParseFileMessage(w);
                if (fmResp?.Folder == null) continue;

                foreach (ProductData? f in fmResp.Folder.Files)
                {
                    if (string.Equals(f.Key, presetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ProtocolClient] Found preset path '{presetPath}': key='{fmResp.Folder.Key}', index={f.Index}");
                        return new PresetLocation(fmResp.Folder.Key ?? string.Empty, f.Index, fmResp.Folder.IsFactory);
                    }
                }
            }

            Console.WriteLine($"[ProtocolClient] Could not find preset path '{presetPath}' in file listings");
            return new PresetLocation(null, -1, false);
        }

        private PresetLocation FindPresetInFileListings(string name, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                WirePayload? w = WaitForMessage(4, TimeSpan.FromMilliseconds(200));
                if (w == null) continue;

                FileMessage? fmResp = TryParseFileMessage(w);
                if (fmResp?.Folder == null) continue;

                foreach (ProductData? f in fmResp.Folder.Files)
                {
                    if (!string.IsNullOrWhiteSpace(f.Name) && f.Name == name)
                    {
                        return new PresetLocation(fmResp.Folder.Key ?? string.Empty, f.Index, fmResp.Folder.IsFactory);
                    }
                }
            }
            return new PresetLocation(null, -1, false);
        }

        private static FileMessage? TryParseFileMessage(WirePayload w)
        {
            try { return FileMessage.Parser.ParseFrom(w.Payload); }
            catch
            {
                Console.WriteLine("[ProtocolClient] Failed to parse FileMessage");
                return null;
            }
        }

        private bool SendSetlistPositionAndVerifyRecall(string folderKey, int folderIndex, bool isFactory, string name, TimeSpan timeout)
        {
            byte[] setlist = ProtobufBuilder.BuildSetlistPositionMessage(folderKey, folderIndex, isFactory);
            SendMessageSafe(setlist, 2, "Failed to send SetlistPosition");

            WirePayload? resp = WaitForMessage(15, timeout);
            if (resp == null) return false;

            try
            {
                RecallPresetMessage rpResp = RecallPresetMessage.Parser.ParseFrom(resp.Payload);
                return rpResp.Preset != null && rpResp.Preset.Name == name;
            }
            catch
            {
                Console.WriteLine($"[ProtocolClient] RecallPreset response parse failed");
                return false;
            }
        }

        private void SendKeepAlive(object? state)
        {
            if (!_connected || _disposed) return;

            try
            {
                // KeepAlive: {f1=1, f3=1}
                byte[] proto = new byte[] { 0x08, 0x01, 0x18, 0x01 };
                SendWireMessage(proto, 32);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProtocolClient] ⚠️ KeepAlive failed: {ex.Message}");
            }
        }

        public IList<ProductData> ListFiles()
        {
            // Drain received messages for File (type 4) entries and parse them.
            List<ProductData> list = [];
            while (_received.Count > 0)
            {
                WirePayload w = _received.Dequeue();
                if (w.MessageType == 4)
                {
                    try
                    {
                        FileMessage fm = FileMessage.Parser.ParseFrom(w.Payload);
                        if (fm.Folder != null)
                        {
                            foreach (ProductData? f in fm.Folder.Files) list.Add(f);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ProtocolClient] ListFiles parse failed: {ex.Message}");
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Cleans up after a connection loss — either detected from within the reader
        /// thread itself (a read stall) or asynchronously from the transport's own
        /// device-watcher thread (an instant physical-unplug notification). Mirrors
        /// <see cref="Disconnect"/> but must NOT join `_readerThread` — that would
        /// deadlock if called from the reader thread itself, and the other caller
        /// (an OS device-list callback) has no reason to wait on it either.
        /// Callable from any thread; idempotent against being invoked twice for the
        /// same drop (e.g. an instant unplug notification followed moments later by
        /// the reader thread's own stall check).
        /// </summary>
        private void HandleConnectionLost(string reason)
        {
            if (!_connected) return;
            _connected = false;

            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _readerThread = null;

            try { _transport.Close(); }
            catch (Exception ex) { Console.WriteLine($"[ProtocolClient] Transport close on connection loss failed: {ex.Message}"); }

            _byType.Clear();
            _lastRecallPreset = null;
            _aesKey = null;
            _aesIv = null;
            _assembler.Reset();

            ConnectionLost?.Invoke(reason);
        }

        public void Disconnect()
        {
            if (!_connected && !_disposed) return;
            _connected = false;

            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            try { _readerThread?.Join(1000); }
            catch (Exception ex) { Console.WriteLine($"[ProtocolClient] Reader thread join failed: {ex.Message}"); }
            _readerThread = null;

            _byType.Clear();
            _lastRecallPreset = null;
            _aesKey = null;
            _aesIv = null;
            _assembler.Reset();

            Console.WriteLine("[ProtocolClient] Disconnected");
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
                Disconnect();
                _transport.Dispose();
            }
        }

        // Produce a short summary of diagnostic counts/timestamps for the last run
        public void PrintRunSummary()
        {
            Console.WriteLine("[ProtocolClient] --- Run Summary ---");
            Console.WriteLine($"[ProtocolClient] ModelRepo: size={_lastModelRepoSize} bytes, at={_lastModelRepoAt}");
            Console.WriteLine("[ProtocolClient] Message counts:");
            foreach (KeyValuePair<uint, long> kv in _messageCounts)
            {
                Console.WriteLine($"  type={kv.Key} count={kv.Value} lastAt={(_lastMessageAt.TryGetValue(kv.Key, out DateTime t) ? t.ToString() : "never")}");
            }
            Console.WriteLine("[ProtocolClient] ---------------------");
        }
    }
}
