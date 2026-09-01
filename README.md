# CortexUSB

A .NET client library for the Neural DSP Quad Cortex's USB HID protocol —
device discovery, the crypto handshake, chunked/compressed protobuf framing,
and a clean high-level API for presets, scenes, and signal-chain grid
editing.

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

## Samples

`samples/ListPresets` and `samples/QueryPreset` are minimal console apps
demonstrating the library against a connected device:

```bash
dotnet run --project samples/ListPresets/ListPresets.csproj
dotnet run --project samples/QueryPreset/QueryPreset.csproj
```

## Device quirks worth knowing

- `SET_REPORT` always STALLs on this device but still processes the data
  successfully — this is expected, not an error.
- USB access is not exclusive across processes; multiple clients can open
  the device concurrently.
- Writes wait for the device's own confirming echo before reporting
  success, since the hardware can silently refuse a write.
- Grid state echoes from the device are sparse deltas — never replace a
  cached grid wholesale from one.


## Acknowledgements

Special thanks to [Simone Margaritelli (a.k.a. EvilSocket)](https://github.com/evilsocket) for the initial research that started all of this and pointing me in the right directions. Also [Jonathan Stokes](https://github.com/stokes-audio) for an incredible reference with the [pyquadcortex](https://github.com/stokes-audio/pyquadcortex) project. Also shoutout to the community over at [OpenCortex](https://discord.com/invite/ef2gBDDSkm).

## License

MIT — see [LICENSE](LICENSE).
