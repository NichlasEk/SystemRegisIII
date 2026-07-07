# SystemRegisIII

SystemRegisIII is an experimental Sega Saturn emulator written in C#.

The first design rule is a modular core:

- `Core/Cpu/Sh2` for the two Hitachi SH-2 CPUs.
- `Core/Bus` for address decoding and device routing.
- `Core/Memory` for RAM, ROM, and memory views.
- `Core/Vdp1` and `Core/Vdp2` for sprite/polygon and background video hardware.
- `Core/Scsp` for Saturn audio.
- `Core/CdBlock` for disc and command handling.
- `Core/SaveState` for deterministic state capture.
- `Host` for platform audio, video, input, and timing adapters.
- `Tools` for trace viewing, ROM inspection, audio exploration, and translation overlays.

Core emulation code should not depend on a UI framework, audio backend, renderer, or network service.
The future UI shell is intentionally undecided; the core is built so any desktop, web, or EutherDrive host can attach later.

## First Bringup

Run a local BIOS trace from the CLI:

```sh
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --instructions 220 --trace
```

The `bios/` directory is intentionally ignored by Git.
