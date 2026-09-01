using System;
using System.Security.Cryptography;

namespace CortexUSB.Client.Encryption
{
    public static class AesGcmHelper
    {
        // Decrypt AES-128-GCM with key, iv, ciphertext+tag. Returns plaintext or throws.
        public static byte[] Decrypt(byte[] key, byte[] iv, byte[] ciphertext, byte[] tag)
        {
            // Use constructor that specifies tag size to avoid SYSLIB0053 obsolete warning.
            // Tag size is 128 bits (16 bytes) for AES-GCM.
            using AesGcm aes = new(key, 128);
            byte[] plaintext = new byte[ciphertext.Length];
            aes.Decrypt(iv, ciphertext, tag, plaintext);
            return plaintext;
        }
    }
}
