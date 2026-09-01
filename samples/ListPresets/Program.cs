using OpenCortex.CortexUSB;
using OpenCortex.CortexUSB.Models;

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

        IReadOnlyList<FlatPreset> presets = cortex.ListPresets();
        List<string> names = presets.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).Distinct().ToList();
        Console.WriteLine($"Found {presets.Count} entries, {names.Count} named presets:");
        foreach (string name in names)
        {
            Console.WriteLine($" - {name}");
        }
    }
}
