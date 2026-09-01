namespace OpenCortex.CortexUSB.Client
{
    /// <summary>
    /// Abstraction for a transport that can send and receive 129-byte HID reports.
    /// Implementations: UsbHidTransport (real device) and ReplayTransport (tests).
    /// </summary>
    public interface ITransport : IDisposable
    {
        public bool Open();
        public bool IsOpen { get; }
        public int QueuedReports { get; }
        // Write a 129-byte HID report (including Report ID at index 0)
        public bool Write(byte[] report);
        // Read a report with timeout (ms). Returns null on timeout.
        public byte[]? Read(int timeoutMs);
        // Tears down the current connection (if any) so a subsequent Open() performs
        // a fresh device enumeration/open rather than short-circuiting on stale state.
        public void Close();
        // Raised the moment the OS reports the opened device is gone (a physical
        // unplug), independent of and much faster than any read-based stall detection.
        public event Action? DeviceRemoved;
    }
}
