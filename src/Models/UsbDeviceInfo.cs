namespace OpenCortex.CortexUSB
{
    /// <summary>
    /// Information about a discovered USB HID device.
    /// </summary>
    public class UsbDeviceInfo
    {
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string Manufacturer { get; set; } = "";
        public string Product { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string DevicePath { get; set; } = "";
    }
}
