using CortexProtobufV2;
using Google.Protobuf;

namespace OpenCortex.CortexUSB.Client
{
    public static class ProtocolMessages
    {
        public static byte[] EncodeWire(byte[] protobufPayload, uint messageType, bool encrypt=false, bool compressed=false)
        {
            byte[] wire = new byte[protobufPayload.Length + 8];
            Array.Copy(protobufPayload, wire, protobufPayload.Length);
            Array.Copy(BitConverter.GetBytes(messageType), 0, wire, protobufPayload.Length, 4);
            wire[protobufPayload.Length + 4] = encrypt ? (byte)1 : (byte)0;
            wire[protobufPayload.Length + 5] = compressed ? (byte)1 : (byte)0;
            wire[protobufPayload.Length + 6] = 0; wire[protobufPayload.Length + 7] = 0;
            return wire;
        }

        public static byte[] BuildVersionRequest()
        {
            // Version request: { f1 = 3 }
            return new byte[] { 0x08, 0x03 };
        }

        public static byte[] BuildVersionReply()
        {
            // {f1=1, f2=1, f11="4.0.0"} — matches Python reference exactly
            VersionMessage msg = new()
            {
                Action = MessageAction.Types.Enum.Update,
                RequestId = 1,
                CortexControlVersion = "4.0.0"
            };
            return msg.ToByteArray();
        }

        public static byte[] BuildResetComms(string sessionId)
        {
            ResetCommsBuffersMessage msg = new() { SessionId = sessionId };
            return msg.ToByteArray();
        }

        public static byte[] BuildConnection()
        {
            return new byte[] { 0x10, 0x01 }; // {f2 = 1}
        }

        public static byte[] BuildModelRepoRequest()
        {
            return new byte[] { 0x08, 0x03 };
        }
    }
}
