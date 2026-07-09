# Saturn Bringup Handoff - 2026-07-09

## Current State

The current NiGHTS bringup blocker is no longer CD authentication or a missing BIOS menu input path. With:

```text
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc "/home/nichlas/roms/Saturn/NightsIntoDreams/NiGHTS into Dreams... (Japan).cue" --dual-sh2 --cd-status standby --instructions 30000000 --summary-only
```

the run still reaches the Work RAM High normalizer loop at `0x06012C84..0x06012C8A`, focused through `PR=0x06011690`. CD Block auth is complete, the mounted NiGHTS disc is detected as auth type `0x04`, and the CD command stream has settled at `Get Hardware Info`/current-status polling rather than exposing a new CD command gap.

## Latest Pushed Checkpoints

- `3d67f32 Expose Saturn transform source probes`
- `e71f8e4 Add Saturn transform matrix probe`

Both are pushed to `origin/main`.

The current diagnostic slice adds targeted matrix-caller, node-builder, node-pool, parent-node, and coefficient-source probes.

## Verified Commands

```text
dotnet format SystemRegisIII.slnx --verify-no-changes
dotnet run --project tests/SystemRegisIII.Smoke/SystemRegisIII.Smoke.csproj
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc "/home/nichlas/roms/Saturn/NightsIntoDreams/NiGHTS into Dreams... (Japan).cue" --dual-sh2 --cd-status standby --instructions 30000000 --summary-only
```

Smoke output:

```text
SystemRegisIII smoke passed.
```

## Evidence

The object/source buffer `0x0605D000..0x0605EFFF` is populated by the routine around `0x0602DC0C` with small signed fixed-point-looking values, for example `0xFFFFD20E`, `0x000008E1`, `0xFFFFFFE0`, `0xFFFFF5E2`, and `0x000008DE`.

The large values do not first appear there. The first large transform writes now point at the transform-matrix/coefficient area `0x06030080..0x0603017F`.

Key observed path:

- Matrix/coefficient suspicious range: `0x06030080..0x0603017F`
- First large matrix writes: around `0x0602E3D6`, `0x0602E3F0`, `0x0602E40A`
- Transform builder path: `0x0602E680..0x0602E7F0`
- Large transform/key writes: `0x0602E70C`, `0x0602E7AA`, `0x0602E7D2`, `0x0602E7EA`
- Geometry copy into normalizer source: `0x0602EA90`
- Normalizer loop: `0x06012C84..0x06012C8A`, focused `PR=0x06011690`

The first large transform write showed `R7=0x060300D4`, so the suspicious MAC source is the transform matrix/coefficient area, not the object-source buffer.

## Disassembly Notes

The matrix pool/stack management around `0x0602E364..0x0602E3A2` appears to allocate/copy 0x30-byte transform blocks through pointers near `0x06030180` and `0x06030184`.

The later large coefficient-producing routines around `0x0602E3D6`, `0x0602E3F0`, and `0x0602E40A` are now ruled out as the first source. Their `MAC.L`/`XTRCT` sequences consume values that are already large.

The large-value chain is now:

- `0x0602DAAC` reads parent-node field `0x0603527C = 0x8B9510EB`.
- `0x0602DAB2` subtracts that value from a small current-node value and produces `R4=0x746E606C`.
- `0x0602E3B0..0x0602E40A` consumes that vector and writes the previously observed large matrix coefficients.
- `0x0603527C` and `0x06035284` are overwritten by `0x0602DF00`, replacing earlier plausible values `0x00044DAF` and `0xFFF695C2` with `0x8B9510EB` and `0xDA5EA506`.
- The `0x0602DE80..0x0602DF00` node-builder loop consumes already-large values from `0x0604156C..0x06041600`.
- The first large coefficient-source writes are shifts at `0x0602DD9C`, `0x0602DDC6`, `0x0602DE16`, `0x0602DE3E`, `0x0602DE66`, and `0x0602DE82`.

Running with `--defer-vblank-in-critical-windows` produces the same parent-node values and write PCs, so periodic VBlank injection is not the cause of this data chain.

No obvious one-line SH-2 semantic fix was proven in the last slice. `XTRCT`, `MAC.L`, `DMULS.L`, signed comparisons, and delayed branch latching already have smoke coverage, though the matrix-builder path may still expose a subtler multiply/divide/shift edge.

## Next Recommended Step

Focus on `0x0602DD80..0x0602DE82` and the inputs feeding those fixed-point shifts.

Recommended approach:

1. Extend the transform-node-builder probe backward from `0x0602DE80` to cover `0x0602DD80..0x0602DE82`.
2. Capture the unshifted source words and their writer/loader addresses before the first large writes into `0x06041584..0x06041598`.
3. Compare the `SHLL`, signed-load, and `DMULS.L` dataflow against the local Mednafen SH-2 reference, but do not patch CPU semantics without a small smoke-test repro.
4. Only patch CPU semantics when a small repro can be expressed as a smoke test.

## Useful Current Probe Output

The `--summary-only` output now includes:

- `Master transform-matrix watch`
- `Master transform-node watch`
- `Master transform-parent-node watch`
- `Master transform-coefficient-source watch`
- `Master transform-key watch`
- `Master transform-source watch`
- `Master geometry-source watch`

The PC summaries also include `Master SH-2 matrix caller probe`, `Master SH-2 matrix builder probe`, and `Master SH-2 transform node builder probe`. Register context now includes `R8`, `R11`, and `R14` in addition to `MACH/MACL`.

Those summaries include hot reads/writes, recent writes, first large writes, recent large writes, and expanded register context including `R1`, `R7`, `R8`, `R11`, and `R14`.
