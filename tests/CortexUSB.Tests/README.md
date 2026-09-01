# CortexUSB Test Suite

## Current status

Both `CortexUSB.Tests` (unit) and `CortexUSB.IntegrationTests` (hardware)
are **source-empty**. This mirrors the state of the original CortexBridge
monorepo test projects, whose test files were deleted deliberately because
they were unreliable, not because coverage is unneeded. There is no
automated test coverage right now; correctness is currently verified by
building the solution and by manual runs of the `ListPresets`/`QueryPreset`
samples against a connected device.

If tests are reintroduced, follow this pattern:
- Unit tests seed `CortexUSB.Client.ReplayTransport` with recorded wire
  messages so `ProtocolClient`/`ProtocolService` can be exercised without
  hardware.
- Hardware-dependent tests live in `CortexUSB.IntegrationTests` and require
  a connected Quad Cortex; they should be treated as opt-in, not part of a
  default `dotnet test` run, given how flaky the USB HID link is.

## Running what exists

```bash
# Build everything
dotnet build CortexUSB.sln

# Exercise the live device manually via the samples
dotnet run --project samples/ListPresets/ListPresets.csproj
dotnet run --project samples/QueryPreset/QueryPreset.csproj
```
