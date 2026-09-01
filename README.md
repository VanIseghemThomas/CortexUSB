# CortexUSB

A .NET client library for the Neural DSP Quad Cortex's USB HID protocol —
device discovery, the crypto handshake, chunked/compressed protobuf framing,
and a clean high-level API for presets, scenes, and signal-chain grid
editing.

This library only talks to the device over USB. It has no opinions about
how you expose that to the outside world — see
[CortexBridge](https://github.com/VanIseghemThomas/CortexBridge) for a
WebSocket server built on top of it, or
[CortexMCP](https://github.com/VanIseghemThomas/CortexMCP) for an MCP server
built on top of it.

## Install

```bash
dotnet add package CortexUSB
```

## Quick start

```csharp
using CortexUSB;

using var cortex = new QuadCortex();
if (await cortex.ConnectAsync())
{
    var state = await cortex.RefreshCurrentStateAsync();
    Console.WriteLine($"Current preset: {state?.CurrentPreset?.Name}");
}
```

`QuadCortex` is the intended entry point — it wraps the lower-level
`ProtocolClient` (wire framing, crypto, chunking) and `ProtocolService`
(state caching, preset library, grid editing) and exposes only clean
domain types. No protobuf, wire, or HID types leak out of it.

## Architecture

- **`Client/`** — transport (`ITransport`, `UsbHidTransport`, `ReplayTransport`
  for tests), the protocol handshake and framing (`ProtocolClient`,
  `WireParser`), and crypto (`Client/Encryption`, XXTEA + AES-GCM).
- **`Protocol/`** — protobuf message building, gzip/zlib decompression, and
  the device's model catalog.
- **`Models/`** — plain domain types (`DeviceState`, `PresetLocation`,
  `UsbDeviceInfo`).
- **`QuadCortex.cs`** / **`ProtocolService.cs`** — the high-level facade and
  the state/preset/grid logic behind it.

## Device quirks worth knowing

- `SET_REPORT` always STALLs on this device but still processes the data
  successfully — this is expected, not an error.
- USB access is not exclusive across processes; multiple clients can open
  the device concurrently.
- Writes wait for the device's own confirming echo before reporting
  success, since the hardware can silently refuse a write.
- Grid state echoes from the device are sparse deltas — never replace a
  cached grid wholesale from one.

## Samples

`samples/ListPresets` and `samples/QueryPreset` are minimal console apps
demonstrating the library against a connected device:

```bash
dotnet run --project samples/ListPresets/ListPresets.csproj
dotnet run --project samples/QueryPreset/QueryPreset.csproj
```

## Testing

See [tests/CortexUSB.Tests/README.md](tests/CortexUSB.Tests/README.md) for
current test status and the intended testing approach.

## License

MIT — see [LICENSE](LICENSE).
