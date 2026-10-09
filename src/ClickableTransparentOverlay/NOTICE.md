# ClickableTransparentOverlay - vendored copy

This folder holds ClickableTransparentOverlay 11.1.0 by zaafar
(https://github.com/zaafar/ClickableTransparentOverlay, tag `11.1.0`, commit `297f3d4`),
licensed under the Apache License 2.0 - see `LICENSE` in this folder.

It is vendored rather than referenced as a package so that PoEformance's model viewer can draw on
the overlay's own Direct3D 11 device: the package keeps the device private and accepts pictures only
as pixels in memory.

## Changes from upstream

Every change is marked in the source with a `// PoEformance:` comment.

- `ClickableTransparentOverlay.csproj` - written for this solution: `net10.0-windows`, ImageSharp
  3.1.12 instead of 3.1.6 (security advisories), nullable analysis off (upstream's own
  annotations do not hold and the solution treats warnings as errors), no packaging settings.
- `Overlay.cs`
  - `Device` and `DeviceContext`: the D3D11 device and its immediate context, protected.
  - `AddView` / `DropView`: hand ImGui a shader-resource view the caller owns, and take it back
    without releasing it.
  - The device is created at the newest of feature levels 11.1, 11.0, 10.1 and 10.0 the hardware
    offers instead of 10.0 alone, and at 10.0 if that request fails.
- `ImGuiRenderer.cs` - `RegisterTexture` and `DeRegisterTexture` are internal instead of private.

Nothing else is changed; the remaining files are upstream's byte for byte.
