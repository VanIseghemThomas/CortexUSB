using System;
using System.IO.Compression;
using System.IO;
using System.Threading;
using System;
using System.Collections.Generic;

namespace CortexUSB.Client
{
    public class WirePayload
    {
        public uint MessageType { get; init; }
        public bool IsEncrypted { get; init; }
        public bool IsCompressed { get; init; }
        public byte[] Payload { get; init; } = [];
    }

    /// <summary>
    /// Parses wire messages of format: [protobuf payload][8-byte trailer]
    /// Trailer: <I B B H> little-endian
    /// Handles gzip/zlib and nested gzip via DecompressRecursive.
    /// </summary>
    public class WireParser
    {
        private const int TRAILER_SIZE = 8;

        private static readonly HashSet<uint> _skipCompressedTypes = LoadSkipCompressedTypes();

        private static HashSet<uint> LoadSkipCompressedTypes()
        {
            string? env = Environment.GetEnvironmentVariable("CORTEX_SKIP_COMPRESSED_TYPES");
            if (string.IsNullOrWhiteSpace(env)) env = "32,33";

            HashSet<uint> set = new();
            foreach (string part in env.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(part.Trim(), out uint v)) set.Add(v);
            }
            return set;
        }

        /// <summary>
        /// Parse wireData and optionally decrypt using AES-GCM key/iv when encrypted.
        /// If aesKey/aesIv are null and an encrypted message is encountered, the raw
        /// encrypted payload is returned (no exception).
        /// </summary>
        public WirePayload Parse(byte[] wireData, byte[]? aesKey = null, byte[]? aesIv = null)
        {
            if (wireData.Length < TRAILER_SIZE) throw new ArgumentException("wireData too short");

            int trailerStart = wireData.Length - TRAILER_SIZE;
            uint messageType = BitConverter.ToUInt32(wireData, trailerStart);
            byte encrypt = wireData[trailerStart + 4];
            byte compressed = wireData[trailerStart + 5];

            int payloadLen = trailerStart;
            byte[] payload = new byte[payloadLen];
            Array.Copy(wireData, 0, payload, 0, payloadLen);

            bool isCompressed = compressed != 0;

            // If encrypted, attempt AES-GCM decryption first (payload format: ciphertext || tag(16))
            bool isEncrypted = encrypt != 0;
            if (isEncrypted)
            {
                if (aesKey != null && aesIv != null && payloadLen > 16)
                {
                    try
                    {
                        int tagLen = 16;
                        byte[] ciphertext = new byte[payloadLen - tagLen];
                        byte[] tag = new byte[tagLen];
                        Array.Copy(payload, 0, ciphertext, 0, ciphertext.Length);
                        Array.Copy(payload, ciphertext.Length, tag, 0, tagLen);
                        payload = Encryption.AesGcmHelper.Decrypt(aesKey, aesIv, ciphertext, tag);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WireParser] AES-GCM decrypt failed, returning raw encrypted payload: {ex.Message}");
                        // leave payload as-is (encrypted)
                    }
                }
                else
                {
                    // No key available — leave payload as-is
                }
            }

            if (isCompressed && payload.Length > 0)
            {
                if (_skipCompressedTypes.Contains(messageType))
                {
                    Console.WriteLine($"[WireParser] Skipping decompression for messageType={messageType} (compressed flag set)");
                }
                else
                {
                    payload = DecompressRecursive(payload, 4);
                }
            }

            return new WirePayload
            {
                MessageType = messageType,
                IsEncrypted = isEncrypted,
                IsCompressed = isCompressed,
                Payload = payload
            };
        }

        private static byte[] DecompressRecursive(byte[] data, int maxDepth)
        {
            byte[] current = data;
            for (int depth = 0; depth < maxDepth; depth++)
            {
                if (current.Length >= 2 && current[0] == 0x1F && current[1] == 0x8B)
                {
                    try
                    {
                        current = DecompressGzip(current);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WireParser] DecompressGzip failed at depth {depth}: {ex.Message}");
                        return data;
                    }
                }

                try
                {
                    current = DecompressZlib(current);
                    continue;
                }
                catch (Exception ex)
                {
                    if (depth == 0)
                    {
                        Console.WriteLine($"[WireParser] DecompressZlib failed at depth {depth}: {ex.Message}");
                        return data;
                    }
                    break;
                }
            }
            return current;
        }

        // Expose helper to recursively scan for nested gzip blocks inside a protobuf
        // field: if a nested gzip magic is detected at an offset, decompress and return.
        public static byte[] TryDecompressNestedGzip(byte[] data)
        {
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 0x1F && data[i+1] == 0x8B)
                {
                    try
                    {
                        // slice from i to end
                        byte[] slice = new byte[data.Length - i];
                        Array.Copy(data, i, slice, 0, slice.Length);
                        using MemoryStream ms = new(slice);
                        using GZipStream gz = new(ms, CompressionMode.Decompress);
                        using MemoryStream outm = new();
                        gz.CopyTo(outm);
                        return outm.ToArray();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WireParser] TryDecompressNestedGzip failed at offset {i}: {ex.Message}");
                    }
                }
            }
            return data;
        }

        private static byte[] DecompressGzip(byte[] compressed)
        {
            using MemoryStream input = new(compressed);
            using GZipStream gz = new(input, CompressionMode.Decompress);
            using MemoryStream outm = new();
            gz.CopyTo(outm);
            return outm.ToArray();
        }

        private static byte[] DecompressZlib(byte[] compressed)
        {
            if (compressed.Length < 6) throw new InvalidDataException("zlib too short");
            using MemoryStream input = new(compressed, 2, compressed.Length - 2);
            using DeflateStream def = new(input, CompressionMode.Decompress);
            using MemoryStream outm = new();
            def.CopyTo(outm);
            return outm.ToArray();
        }
    }
}
