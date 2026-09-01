using HidSharp;
using System.Collections.Concurrent;
using System.Linq;

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

        private HidStream? _stream;
        private readonly ConcurrentQueue<byte[]> _incomingReports = new();
        private CancellationTokenSource _cancellationTokenSource = new();
        private Task? _readerTask;
        private readonly object _writeLock = new();
        private volatile int _totalReportsReceived;
        private volatile int _overflowCount;
        private bool _disposed;
        private volatile string? _openDevicePath;

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
                    Console.WriteLine("[UsbTransport] ⚡ Device removal detected (instant, via OS device-list change)");
                    DeviceRemoved?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UsbTransport] DeviceListChanged handler error: {ex.Message}");
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
                    
                        try { manufacturer = device.GetManufacturer(); } catch (Exception ex) { Console.WriteLine($"[UsbTransport] GetManufacturer failed: {ex.Message}"); }
                        try { product = device.GetProductName(); } catch (Exception ex) { Console.WriteLine($"[UsbTransport] GetProductName failed: {ex.Message}"); }
                        try { serialNumber = device.GetSerialNumber(); } catch (Exception ex) { Console.WriteLine($"[UsbTransport] GetSerialNumber failed: {ex.Message}"); }
                    
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
                        Console.WriteLine($"[UsbTransport] Error getting device info: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UsbTransport] Error enumerating devices: {ex.Message}");
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
                Console.WriteLine("[UsbTransport] Device already open");
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
                        Console.WriteLine($"[UsbTransport] Found device: {device.GetProductName()}");
                        Console.WriteLine($"[UsbTransport] Path: {device.DevicePath}");
                        Console.WriteLine($"[UsbTransport] Max input: {device.GetMaxInputReportLength()}");
                        Console.WriteLine($"[UsbTransport] Max output: {device.GetMaxOutputReportLength()}");

                        // Try to open the device
                        if (device.TryOpen(out _stream))
                        {
                            _stream.ReadTimeout = 200; // 200ms timeout for reads

                            // Start background reader thread
                            _readerTask = Task.Run(() => ReaderLoop(_cancellationTokenSource.Token));

                            // Watch for this exact device disappearing from the OS's HID
                            // device list, so an unplug is detected instantly rather than
                            // only after several seconds of read silence.
                            _openDevicePath = device.DevicePath;
                            DeviceList.Local.Changed += OnDeviceListChanged;

                            Console.WriteLine("[UsbTransport] ✅ Device opened successfully");
                            return true;
                        }
                        else
                        {
                            Console.WriteLine("[UsbTransport] Failed to open device stream");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UsbTransport] Error opening device: {ex.Message}");
                    }
                }

                Console.WriteLine("[UsbTransport] No Quad Cortex device found or accessible");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UsbTransport] Error during device discovery: {ex.Message}");
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
                Console.WriteLine("[UsbTransport] Cannot write: device not connected");
                return false;
            }

            if (data.Length != REPORT_SIZE)
            {
                Console.WriteLine($"[UsbTransport] Invalid report size: {data.Length}, expected {REPORT_SIZE}");
                return false;
            }

            if (data[0] != REPORT_ID_OUTPUT)
            {
                Console.WriteLine($"[UsbTransport] Warning: Report ID is 0x{data[0]:X2}, expected 0x{REPORT_ID_OUTPUT:X2}");
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
                    Console.WriteLine($"[UsbTransport] Write IOException (may be expected STALL): {ioEx.Message}");
                    return true; // Treat as success - device quirk
                }
                catch (TimeoutException)
                {
                    // Timeout during write might also mean STALL - treat as success
                    Console.WriteLine($"[UsbTransport] Write timeout");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UsbTransport] Write exception: {ex.GetType().Name} - {ex.Message}");
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

            try { _cancellationTokenSource.Cancel(); }
            catch (Exception ex) { Console.WriteLine($"[UsbTransport] Close: cancel failed: {ex.Message}"); }

            try { _readerTask?.Wait(TimeSpan.FromSeconds(1)); }
            catch { /* reader may not respond promptly to a dead stream; expected */ }
            _readerTask = null;

            _stream?.Dispose();
            _stream = null;

            DeviceList.Local.Changed -= OnDeviceListChanged;
            _openDevicePath = null;

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            while (_incomingReports.TryDequeue(out _)) { }

            Console.WriteLine("[UsbTransport] Closed (ready for reopen)");
        }

        /// <summary>
        /// Background reader thread that continuously reads from the device.
        /// </summary>
        private void ReaderLoop(CancellationToken cancellationToken)
        {
            Console.WriteLine("[UsbTransport] Reader thread started");
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
                            Console.WriteLine("[UsbTransport] ⚠️ Queue overflow! Report dropped.");
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
                        Console.WriteLine($"[UsbTransport] IO error in reader: {ioEx.Message}");
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"[UsbTransport] Reader exception: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }
            }

            Console.WriteLine("[UsbTransport] Reader thread stopped");
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
                _cancellationTokenSource.Dispose();
                Console.WriteLine("[UsbTransport] Disposed");
            }
        }
    }
}
