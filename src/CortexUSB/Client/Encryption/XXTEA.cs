using System;

namespace CortexUSB.Client.Encryption
{
    // Minimal XXTEA implementation for decrypting key_material blobs from device.
    // This matches the simple reference XXTEA algorithm (block cipher operating on
    // uint32 array with little-endian conversions).
    public static class XXTEA
    {
        public static byte[] Decrypt(byte[] data, byte[] key)
        {
            if (data == null || data.Length == 0) return [];
            // Must be multiple of 4
            int n = data.Length / 4;
            uint[] v = new uint[n];
            for (int i = 0; i < n; i++) v[i] = BitConverter.ToUInt32(data, i * 4);

            uint[] k = new uint[4];
            for (int i = 0; i < 4; i++) k[i] = 0u;
            for (int i = 0; i < Math.Min(16, key.Length); i += 4)
            {
                int idx = i / 4;
                if (i + 4 <= key.Length) k[idx] = BitConverter.ToUInt32(key, i);
            }

            uint[] result = DecryptUIntArray(v, k);
            byte[] outb = new byte[result.Length * 4];
            for (int i = 0; i < result.Length; i++) Array.Copy(BitConverter.GetBytes(result[i]), 0, outb, i*4, 4);
            return outb;
        }

        // Classic XXTEA decrypt (from TEA family)
        private static uint[] DecryptUIntArray(uint[] v, uint[] k)
        {
            int n = v.Length;
            if (n < 1) return v;
            uint rounds = (uint)(6 + 52 / n);
            uint sum = rounds * 0x9E3779B9u;
            while (sum != 0)
            {
                uint e = (sum >> 2) & 3;
                for (int p = n - 1; p >= 0; p--)
                {
                    uint z = v[(p - 1 + n) % n];
                    uint y = v[p];
                    uint mx = ((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4)) ^ ((sum ^ y) + (k[(p & 3) ^ e] ^ z));
                    v[p] -= mx;
                }
                sum -= 0x9E3779B9u;
            }
            return v;
        }
    }
}
