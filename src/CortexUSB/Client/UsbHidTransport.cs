using System;
using CortexUSB;

namespace CortexUSB.Client
{
    /// <summary>
    /// Thin adapter that implements ITransport by delegating to existing UsbTransport.
    /// This avoids touching the existing UsbTransport API while allowing the new
    /// client code to depend on ITransport.
    /// </summary>
    public class UsbHidTransport : ITransport
    {
        private readonly UsbTransport _inner = new();
        private bool _disposed;

        public bool Open() => _inner.Open();

        public bool IsOpen => _inner.IsConnected;

        public int QueuedReports => _inner.QueuedReports;

        public bool Write(byte[] report) => _inner.Write(report);

        public byte[]? Read(int timeoutMs) => _inner.Read(timeoutMs);

        public void Close() => _inner.Close();

        public event Action? DeviceRemoved
        {
            add => _inner.DeviceRemoved += value;
            remove => _inner.DeviceRemoved -= value;
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
                _inner.Dispose();
            }
        }
    }
}
