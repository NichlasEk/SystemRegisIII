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

The bus is page-mapped for the hot path. Tracing is attached as an optional bus wrapper instead of being built into every device lookup.
`SaturnSystemMap.CreateBringup` builds the early BIOS map with RAM aliases and named MMIO stubs so CLI runs can report which missing hardware blocks were touched.

## First Bringup

Run a local BIOS trace from the CLI:

```sh
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --instructions 220 --trace
```

The `bios/` directory is intentionally ignored by Git.
Without `--trace`, the CLI prints a compact bringup summary with the current PC, first/last unimplemented opcode if any, and touched stub devices.

The bringup CLI can render the richest completed VDP1 command list captured at VBlank to a binary PPM image:

```sh
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc "/path/to/game.cue" --dual-sh2 --simulate-scsp-command-ack --vblank-interval 100000 --instructions 30000000 --summary-only --dump-vdp1-frame /tmp/saturn-vdp1.ppm
```

Add `--dump-vdp1-texture /tmp/saturn-vdp1-texture.bin` to dump the largest captured normal-sprite texture as raw VDP1 bytes for reference-emulator comparisons.
