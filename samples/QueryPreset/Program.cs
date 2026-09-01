using OpenCortex.CortexUSB;

public static class Program
{
    private static async Task Main()
    {
        List<UsbDeviceInfo> devices = UsbTransport.EnumerateDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return;
        }

        using QuadCortex cortex = new();

        Console.WriteLine("Connecting...");
        if (!await cortex.ConnectAsync(TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine("Failed to connect");
            return;
        }

        string? name = cortex.GetStateSummary().PresetDetails?.Name;
        Console.WriteLine(name == null ? "Current preset: <null>" : $"Current preset: '{name}'");
    }
}
