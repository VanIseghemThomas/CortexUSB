using System.IO.Compression;

namespace OpenCortex.CortexUSB.Protocol
{
    public static class CompressionUtils
    {
        public static byte[] DecompressIfNeeded(byte[] data)
        {
            if (data.Length < 2)
            {
                return data;
            }

            if (data[0] == 0x1F && data[1] == 0x8B)
            {
                try
                {
                    return DecompressGzip(data);
                }
                catch
                {
                    return data;
                }
            }

            try
            {
                return DecompressZlib(data);
            }
            catch
            {
                return data;
            }
        }

        private static byte[] DecompressGzip(byte[] compressed)
        {
            using MemoryStream inputStream = new(compressed);
            using GZipStream gzipStream = new(inputStream, CompressionMode.Decompress);
            using MemoryStream outputStream = new();
            gzipStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        private static byte[] DecompressZlib(byte[] compressed)
        {
            if (compressed.Length < 6)
            {
                throw new InvalidDataException("Zlib data too short");
            }

            using MemoryStream inputStream = new(compressed, 2, compressed.Length - 2);
            using DeflateStream deflateStream = new(inputStream, CompressionMode.Decompress);
            using MemoryStream outputStream = new();
            deflateStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }
    }
}
