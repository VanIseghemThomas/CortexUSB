using System;
using OpenCortex.CortexUSB.Client;
using Microsoft.Extensions.Logging;

class Program
{
    static void Main(string[] args)
    {
        var devices = OpenCortex.CortexUSB.UsbTransport.EnumerateDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return;
        }

        using var transport = new OpenCortex.CortexUSB.Client.UsbHidTransport();
        var logger = new SimpleConsoleLogger<ProtocolClient>();
        using var client = new ProtocolClient(transport, logger: logger, fetchModelRepoInHandshake: false);

        Console.WriteLine("Connecting...");
        if (!client.Connect(TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine("Failed to connect");
            return;
        }

        var preset = client.GetCurrentPreset(TimeSpan.FromSeconds(10));
        if (preset == null)
        {
            Console.WriteLine("Current preset: <null>");
        }
        else
        {
            Console.WriteLine($"Current preset: '{preset.Name}'");
        }

        client.PrintRunSummary();
    }
}
