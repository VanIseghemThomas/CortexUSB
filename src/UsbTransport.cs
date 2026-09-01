using HidSharp;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenCortex.CortexUSB.Client;

namespace OpenCortex.CortexUSB
{
    /// <summary>
    /// Pure .NET USB HID transport for Quad Cortex using HidSharp library.
    /// Handles the device's quirky behavior where SET_REPORT always STALLs but processes data successfully.
    /// </summary>
    public class UsbTransport : IDisposable
    {
        // Quad Cortex USB identifiers
        private const int VENDOR_ID = 0x152A;
        private const int PRODUCT_ID = 0x880A;
        private const int REPORT_SIZE = 129; // 1 byte Report ID + 128 bytes data
        private const byte REPORT_ID_OUTPUT = 0x02;
        private const int QUEUE_CAPACITY = 1024;

        // Used only by the static EnumerateDevices() helper, which has no instance to carry a
        // per-call logger for. Kept separate from the instance _logger below.
        private static readonly ILogger<UsbTransport> _staticLogger = new SimpleConsoleLogger<UsbTransport>();

        private readonly ILogger<UsbTransport> _logger;
        private HidStream? _stream;
        private readonly ConcurrentQueue<byte[]> _incomingReports = new();
        private CancellationTokenSource _cts = new();
        private Task? _readerTask;
        private readonly object _writeLock = new();
        private volatile int _totalReportsReceived;
        private volatile int _overflowCount;
        private bool _disposed;
        private volatile string? _openDevicePath;

        public UsbTransport(ILogger<UsbTransport>? logger = null)
        {
            _logger = logger ?? new SimpleConsoleLogger<UsbTransport>();
        }

        public bool IsConnected => _stream != null && _stream.CanRead && !_disposed;
        public int QueuedReports => _incomingReports.Count;
        public int TotalReportsReceived => _totalReportsReceived;
        public int OverflowCount => _overflowCount;

        /// <summary>
        /// Raised (on HidSharp's own device-watcher thread, not any reader thread)
        /// the moment the OS reports our opened device is no longer present — a
        /// physical unplug. This fires near-instantly, unlike the read-stall
        /// detector upstream in <see cref="Client.ProtocolClient"/>, which only
        /// notices an already-dead link after several seconds of silence.
        /// </summary>
        public event Action? DeviceRemoved;

        private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
        {
            string? openPath = _openDevicePath;
            if (openPath == null) return;

            try
            {
                bool stillPresent = DeviceList.Local
                    .GetHidDevices(VENDOR_ID, PRODUCT_ID)
                    .Any(d => d.DevicePath == openPath);

                if (!stillPresent)
                {
                    _logger.LogInformation("[UsbTransport] Device removal detected (instant, via OS device-list change)");
                    DeviceRemoved?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UsbTransport] DeviceListChanged handler error");
            }
        }

        /// <summary>
        /// Enumerates all connected Quad Cortex devices.
        /// </summary>
        public static List<UsbDeviceInfo> EnumerateDevices()
        {
            List<UsbDeviceInfo> devices = new();

            try
            {
                DeviceList deviceList = DeviceList.Local;
                IEnumerable<HidDevice> hidDevices = deviceList.GetHidDevices(VENDOR_ID, PRODUCT_ID);

                foreach (HidDevice? device in hidDevices)
                {
                    try
                    {
                        string? manufacturer = null;
                        string? product = null;
                        string? serialNumber = null;
                    
                        try { manufacturer = device.GetManufacturer(); } catch (Exception ex) { _staticLogger.LogWarning(ex, "[UsbTransport] GetManufacturer failed"); }
                        try { product = device.GetProductName(); } catch (Exception ex) { _staticLogger.LogWarning(ex, "[UsbTransport] GetProductName failed"); }
                        try { serialNumber = device.GetSerialNumber(); } catch (Exception ex) { _staticLogger.LogWarning(ex, "[UsbTransport] GetSerialNumber failed"); }
                    
                        devices.Add(new UsbDeviceInfo
                        {
                            VendorId = device.VendorID,
                            ProductId = device.ProductID,
                            Manufacturer = manufacturer ?? "Unknown",
                            Product = product ?? "Unknown",
                            SerialNumber = serialNumber ?? "Unknown",
                            DevicePath = device.DevicePath
                        });
                    }
                    catch (Exception ex)
                    {
                        _staticLogger.LogWarning(ex, "[UsbTransport] Error getting device info");
                    }
                }
            }
            catch (Exception ex)
            {
                _staticLogger.LogWarning(ex, "[UsbTransport] Error enumerating devices");
            }

            return devices;
        }

        /// <summary>
        /// Opens the first available Quad Cortex device.
        /// </summary>
        public bool Open()
        {
            if (IsConnected)
            {
                _logger.LogDebug("[UsbTransport] Device already open");
                return true;
            }

            try
            {
                DeviceList deviceList = DeviceList.Local;
                IEnumerable<HidDevice> hidDevices = deviceList.GetHidDevices(VENDOR_ID, PRODUCT_ID);

                foreach (HidDevice? device in hidDevices)
                {
                    try
                    {
                        _logger.LogDebug("[UsbTransport] Found device: {Product}", device.GetProductName());
                        _logger.LogDebug("[UsbTransport] Path: {DevicePath}", device.DevicePath);
                        _logger.LogDebug("[UsbTransport] Max input: {MaxInput}", device.GetMaxInputReportLength());
                        _logger.LogDebug("[UsbTransport] Max output: {MaxOutput}", device.GetMaxOutputReportLength());

                        // Try to open the device
                        if (device.TryOpen(out _stream))
                        {
                            _stream.ReadTimeout = 200; // 200ms timeout for reads

                            // Start background reader thread
                            _readerTask = Task.Run(() => ReaderLoop(_cts.Token), _cts.Token);

                            // Watch for this exact device disappearing from the OS's HID
                            // device list, so an unplug is detected instantly rather than
                            // only after several seconds of read silence.
                            _openDevicePath = device.DevicePath;
                            DeviceList.Local.Changed += OnDeviceListChanged;

                            _logger.LogInformation("[UsbTransport] Device opened successfully");
                            return true;
                        }
                        else
                        {
                            _logger.LogWarning("[UsbTransport] Failed to open device stream");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[UsbTransport] Error opening device");
                    }
                }

                _logger.LogWarning("[UsbTransport] No Quad Cortex device found or accessible");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UsbTransport] Error during device discovery");
                return false;
            }
        }

        /// <summary>
        /// Writes a 129-byte HID output report to the device.
        /// The device STALLs SET_REPORT requests, but we catch the exception and treat it as success.
        /// </summary>
        public bool Write(byte[] data)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("[UsbTransport] Cannot write: device not connected");
                return false;
            }

            if (data.Length != REPORT_SIZE)
            {
                _logger.LogWarning("[UsbTransport] Invalid report size: {ActualSize}, expected {ExpectedSize}", data.Length, REPORT_SIZE);
                return false;
            }

            if (data[0] != REPORT_ID_OUTPUT)
            {
                _logger.LogWarning("[UsbTransport] Report ID is 0x{ActualId:X2}, expected 0x{ExpectedId:X2}", data[0], REPORT_ID_OUTPUT);
            }

            lock (_writeLock)
            {
                try
                {
                    // Use Write() for Output Reports (not SetFeature which is for Feature Reports)
                    // The native C library uses IOHIDReportTypeOutput which corresponds to Write()
                    _stream!.Write(data);
                    return true;
                }
                catch (IOException ioEx)
                {
                    // CRITICAL: IOException with STALL is EXPECTED behavior!
                    // The device processes the data even though it returns STALL error
                    _logger.LogWarning(ioEx, "[UsbTransport] Write IOException (may be expected STALL)");
                    return true; // Treat as success - device quirk
                }
                catch (TimeoutException ex)
                {
                    // Timeout during write might also mean STALL - treat as success
                    _logger.LogWarning(ex, "[UsbTransport] Write timeout");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[UsbTransport] Write exception: {ExceptionType}", ex.GetType().Name);
                    // For any other exception, assume it worked (device quirk)
                    return true;
                }
            }
        }

        /// <summary>
        /// Reads the next available HID input report from the queue.
        /// </summary>
        public byte[]? Read()
        {
            return _incomingReports.TryDequeue(out byte[]? report) ? report : null;
        }

        /// <summary>
        /// Reads a report with timeout.
        /// </summary>
        public byte[]? Read(int timeoutMs)
        {
            if (_incomingReports.TryDequeue(out byte[]? report))
                return report;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                if (_incomingReports.TryDequeue(out report))
                    return report;
            }

            return null;
        }

        /// <summary>
        /// Tears down the current stream/reader without disposing the transport, so a
        /// subsequent <see cref="Open"/> re-enumerates and opens a fresh device handle.
        /// <see cref="IsConnected"/> does not proactively detect a physical unplug (the
        /// underlying stream can keep reporting <c>CanRead == true</c>), so callers that
        /// have independently detected a dead link (e.g. a read stall upstream) must call
        /// this before retrying <see cref="Open"/> — otherwise Open's own "already
        /// connected" short-circuit would skip re-opening entirely.
        /// </summary>
        public void Close()
        {
            if (_stream == null && _readerTask == null) return;

            try { _cts.Cancel(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[UsbTransport] Close: cancel failed"); }

            try { _readerTask?.Wait(TimeSpan.FromSeconds(1), _cts.Token); }
            catch { /* reader may not respond promptly to a dead stream; expected */ }
            _readerTask = null;

            _stream?.Dispose();
            _stream = null;

            DeviceList.Local.Changed -= OnDeviceListChanged;
            _openDevicePath = null;

            _cts.Dispose();
            _cts = new CancellationTokenSource();

            while (_incomingReports.TryDequeue(out _))
            {
                // NOP
            }

            _logger.LogInformation("[UsbTransport] Closed (ready for reopen)");
        }

        /// <summary>
        /// Background reader thread that continuously reads from the device.
        /// </summary>
        private void ReaderLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("[UsbTransport] Reader thread started");
            byte[] buffer = new byte[REPORT_SIZE];

            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                try
                {
                    // Read with timeout (configured in stream)
                    int bytesRead = _stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        // Copy report to avoid buffer reuse issues
                        byte[] report = new byte[bytesRead];
                        Array.Copy(buffer, report, bytesRead);

                        // Add to queue with overflow protection
                        if (_incomingReports.Count < QUEUE_CAPACITY)
                        {
                            _incomingReports.Enqueue(report);
                            Interlocked.Increment(ref _totalReportsReceived);
                        }
                        else
                        {
                            Interlocked.Increment(ref _overflowCount);
                            _logger.LogWarning("[UsbTransport] Queue overflow! Report dropped.");
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Normal - no data available, continue loop
                }
                catch (IOException ioEx)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ioEx, "[UsbTransport] IO error in reader");
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "[UsbTransport] Reader exception");
                        Thread.Sleep(100);
                    }
                }
            }

            _logger.LogDebug("[UsbTransport] Reader thread stopped");
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
                Close();
                _cts.Dispose();
                _logger.LogDebug("[UsbTransport] Disposed");
            }
        }
    }
}
