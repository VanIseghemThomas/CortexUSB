using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenCortex.CortexUSB.Client;

namespace OpenCortex.CortexUSB
{
    /// <summary>
    /// Assembles chunked HID reports into complete wire messages.
    /// Protocol: 129 bytes = [ReportID:1][Header:2][Payload:126]
    /// Header bits: 15=LAST, 14=FIRST, 0-13=length (mask 0x3FFF)
    ///
    /// Handles interleaved single-chunk messages (e.g. Tempo heartbeats during a large
    /// multi-chunk ModelRepo transfer) by queuing them without disturbing the in-progress
    /// multi-chunk buffer, exactly as the Python reference client does.
    /// </summary>
    public class ChunkAssembler
    {
        private const int REPORT_SIZE = 129;
        private const byte REPORT_ID_INPUT = 0x01;
        private const int MAX_PAYLOAD_PER_CHUNK = 126;

        // Header flags
        private const ushort FLAG_LAST  = 0x8000;
        private const ushort FLAG_FIRST = 0x4000;
        private const ushort LENGTH_MASK = 0x3FFF; // Bits 0-13 for length (max 16383, but capped at 126 per chunk)

        // Multi-chunk assembly buffer — only active between a FIRST chunk and its LAST chunk.
        private readonly List<byte> _buffer = [];
        private bool _assembling = false;

        // Single-chunk messages that arrived while a multi-chunk transfer was in progress.
        private readonly Queue<byte[]> _pendingCompleted = new();

        private readonly object _lock = new();

        // Statistics
        private volatile int _totalChunksProcessed;
        private volatile int _messagesAssembled;

        private readonly ILogger<ChunkAssembler> _logger;

        public ChunkAssembler(ILogger<ChunkAssembler>? logger = null)
        {
            _logger = logger ?? new SimpleConsoleLogger<ChunkAssembler>();
        }

        /// <summary>
        /// Processes a single HID report chunk.
        ///
        /// Returns a complete message payload when one is ready, or null when more chunks
        /// are needed.  If a single-chunk message arrives during a multi-chunk transfer it
        /// is queued internally; call <see cref="TryDequeue"/> to drain those as well.
        /// </summary>
        public byte[]? ProcessChunk(byte[] chunk)
        {
            if (chunk.Length != REPORT_SIZE)
            {
                throw new ArgumentException($"Invalid chunk size: {chunk.Length}, expected {REPORT_SIZE} bytes");
            }

            if (chunk[0] != REPORT_ID_INPUT)
            {
                throw new ArgumentException($"Invalid Report ID: 0x{chunk[0]:X2}, expected 0x{REPORT_ID_INPUT:X2}");
            }

            lock (_lock)
            {
                Interlocked.Increment(ref _totalChunksProcessed);

                // Parse header (little-endian, bytes 1-2 after the report ID byte)
                ushort header = (ushort)(chunk[1] | (chunk[2] << 8));

                bool isFirst = (header & FLAG_FIRST) != 0;
                bool isLast  = (header & FLAG_LAST)  != 0;
                int payloadLength = header & LENGTH_MASK;

                // Validate payload length
                if (payloadLength > MAX_PAYLOAD_PER_CHUNK)
                {
                    _logger.LogWarning("[ChunkAssembler] Warning: payload length {PayloadLength} exceeds max {MaxPayloadPerChunk}, clamping", payloadLength, MAX_PAYLOAD_PER_CHUNK);
                    payloadLength = MAX_PAYLOAD_PER_CHUNK;
                }

                // Extract payload bytes
                byte[] payload = new byte[payloadLength];
                Array.Copy(chunk, 3, payload, 0, payloadLength);

                // ── Single-chunk message (FIRST | LAST) ──────────────────────────────
                if (isFirst && isLast)
                {
                    Interlocked.Increment(ref _messagesAssembled);

                    if (_assembling)
                    {
                        // A multi-chunk transfer is in progress — queue this completed
                        // message rather than returning it immediately so that the caller
                        // continues to receive the in-progress message stream undisturbed.
                        _pendingCompleted.Enqueue(payload);
                        return null;
                    }

                    // No multi-chunk transfer in progress — return immediately.
                    return payload;
                }

                // ── FIRST chunk of a multi-chunk message ─────────────────────────────
                if (isFirst)
                {
                    if (_assembling)
                    {
                        _logger.LogWarning("[ChunkAssembler] Warning: new FIRST chunk while assembling, discarding {BufferedByteCount} buffered bytes", _buffer.Count);
                    }
                    _buffer.Clear();
                    _buffer.AddRange(payload);
                    _assembling = true;
                    return null;
                }

                // ── Middle or LAST chunk ──────────────────────────────────────────────
                if (!_assembling)
                {
                    _logger.LogWarning("[ChunkAssembler] Warning: received continuation chunk without active assembly — ignoring");
                    return null;
                }

                _buffer.AddRange(payload);

                if (isLast)
                {
                    byte[] complete = _buffer.ToArray();
                    _buffer.Clear();
                    _assembling = false;
                    Interlocked.Increment(ref _messagesAssembled);

                    // Before returning the multi-chunk result, flush any queued
                    // single-chunk messages to the pending queue so the caller can
                    // drain them via TryDequeue after processing this one.
                    // (The caller must call TryDequeue in a loop after each non-null
                    // return to fully drain the pending queue.)
                    return complete;
                }

                // Middle chunk — continue buffering
                return null;
            }
        }

        /// <summary>
        /// Dequeues a single-chunk message that was queued while a multi-chunk
        /// transfer was in progress.  Call this in a loop after each non-null
        /// return from <see cref="ProcessChunk"/> to drain any interleaved messages.
        /// Returns null when the queue is empty.
        /// </summary>
        public byte[]? TryDequeue()
        {
            lock (_lock)
            {
                return _pendingCompleted.Count > 0 ? _pendingCompleted.Dequeue() : null;
            }
        }

        /// <summary>
        /// Gets current assembly statistics.
        /// </summary>
        public ChunkAssemblerStatistics GetStatistics()
        {
            lock (_lock)
            {
                return new ChunkAssemblerStatistics
                {
                    TotalChunksProcessed = _totalChunksProcessed,
                    MessagesAssembled    = _messagesAssembled,
                    BufferedBytes        = _buffer.Count,
                    PendingCompletedMessages = _pendingCompleted.Count
                };
            }
        }

        /// <summary>
        /// Clears any buffered data and the pending queue (useful for error recovery).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _pendingCompleted.Clear();
                _assembling = false;
            }
        }
    }

    /// <summary>
    /// Statistics about chunk assembly operations.
    /// </summary>
    public class ChunkAssemblerStatistics
    {
        public int TotalChunksProcessed     { get; set; }
        public int MessagesAssembled        { get; set; }
        public int BufferedBytes            { get; set; }
        public int PendingCompletedMessages { get; set; }
    }
}
