using System;
using System.Collections.Generic;
using System.Linq;
using OpenCortex.CortexUSB.Client;
using CortexProtobufV2;

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
        using var client = new ProtocolClient(transport, fetchModelRepoInHandshake: true);

        Console.WriteLine("Connecting...");
        if (!client.Connect(TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine("Failed to connect");
            return;
        }

        Console.WriteLine("Requesting file list...");
        var files = client.GetLoadedPresets(TimeSpan.FromSeconds(10));
        Console.WriteLine($"Found {files.Count} file entries");

        // Print all non-empty names
        var names = files.Where(f => !string.IsNullOrWhiteSpace(f.Name)).Select(f => f.Name).Distinct().ToList();
        Console.WriteLine($"Found {names.Count} named presets:");
        foreach (var n in names)
        {
            Console.WriteLine($" - {n}");
        }

        client.PrintRunSummary();
    }
}
