namespace OpenCortex.CortexUSB.Client
{
    /// <summary>
    /// Transport for tests that replays pre-recorded 129-byte HID reports.
    /// </summary>
    public class ReplayTransport : ITransport
    {
        private readonly Queue<byte[]> _queue = new();
        private bool _open;
        private bool _disposed;

        public ReplayTransport(IEnumerable<byte[]> reports)
        {
            foreach (byte[] r in reports)
                _queue.Enqueue(r);
        }

        /// <summary>
        /// Convenience factory: take high-level wire messages (protobuf+trailer) and
        /// convert them into 129-byte HID input reports expected by the replay
        /// transport. This avoids test code manually constructing HID framing.
        /// </summary>
        public static ReplayTransport FromWireMessages(IEnumerable<byte[]> wireMessages)
        {
            List<byte[]> reports = new();
            foreach (byte[] wire in wireMessages)
            {
                byte[] chunk = new byte[129];
                chunk[0] = 0x01; // Input report
                ushort header = (ushort)(0xC000 | wire.Length);
                chunk[1] = (byte)(header & 0xFF);
                chunk[2] = (byte)((header >> 8) & 0xFF);
                Array.Copy(wire, 0, chunk, 3, wire.Length);
                reports.Add(chunk);
            }
            return new ReplayTransport(reports);
        }

        public bool Open()
        {
            _open = true;
            return true;
        }

        public bool IsOpen => _open;

        public int QueuedReports => _queue.Count;

        public bool Write(byte[] report)
        {
            // No-op for replay transport; just log optionally
            return true;
        }

        public byte[]? Read(int timeoutMs)
        {
            if (_queue.Count == 0)
            {
                Thread.Sleep(Math.Min(timeoutMs, 50));
                return null;
            }
            return _queue.Dequeue();
        }

        public void Close()
        {
            _open = false;
        }

        // Replay has no real device to lose; never fires.
        public event Action? DeviceRemoved { add { } remove { } }

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
                _open = false;
            }
        }
    }
}
