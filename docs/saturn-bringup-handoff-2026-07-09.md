# Saturn Bringup Handoff - 2026-07-09

## Current State

The old NiGHTS transform/normalizer blocker is fixed. The game uses the SH-2 on-chip division unit through `0xFFFFFF00`, but `Sh2InternalRegisterBus` previously treated those registers as passive storage. The fixed-point helper at `0x0601157C` therefore returned the written dividend low word instead of the signed 64/32 quotient, which caused the large transform values and the long normalizer loop.

With:

```text
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc "/home/nichlas/roms/Saturn/NightsIntoDreams/NiGHTS into Dreams... (Japan).cue" --dual-sh2 --cd-status standby --instructions 30000000 --summary-only
```

the 30M run now reaches `PC=0x060110C4` instead of `0x06012C88`, and normalizer-probe hits fall from about 916,000 to about 81,000. The formerly corrupt parent-node values are now small signed fixed-point values.

After about 130 simulated frames NiGHTS reaches a separate SCSP sound-driver handshake at sound RAM `0x05A00700`. Since no 68k/SCSP CPU core runs yet, `--simulate-scsp-command-ack` explicitly supplies that bringup-only acknowledgement. With that flag and `--vblank-interval 100000`, a 50M run passes both old loops, increases Work RAM High writes to about 11.6 million, and spends its tail in returning BIOS byte-copy calls rather than a stable wait loop.

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
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc "/home/nichlas/roms/Saturn/NightsIntoDreams/NiGHTS into Dreams... (Japan).cue" --dual-sh2 --cd-status standby --simulate-scsp-command-ack --vblank-interval 100000 --instructions 50000000 --summary-only
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

The missing hardware behavior was the SH-2 DIVU register block:

- `0xFFFFFF00`: signed divisor (`DVSR`)
- `0xFFFFFF04`: signed 32/32 dividend trigger/result (`DVDNT`)
- `0xFFFFFF10`: signed 64-bit dividend high/remainder (`DVDNTH`)
- `0xFFFFFF14`: signed 64-bit dividend low trigger/quotient (`DVDNTL`)
- `0xFFFFFF18..1C`: result shadows

The implementation now handles normal signed 32/32 and 64/32 division, remainder, mirrored register addresses, and saturating overflow status. Smoke coverage includes the exact NiGHTS-shaped `0xFFFFFFFE_79880000 / 0x00220000 = -2940` case.

No obvious one-line SH-2 semantic fix was proven in the last slice. `XTRCT`, `MAC.L`, `DMULS.L`, signed comparisons, and delayed branch latching already have smoke coverage, though the matrix-builder path may still expose a subtler multiply/divide/shift edge.

## Next Recommended Step

An accelerated 80M run initially appeared to reach a stable tail at `0x0602941E..0x06029422` after the BIOS copy/decompression work. A focused post-load probe proved this is an intentional short delay inside the SCSP command routine: `ADD #1,R4`, `CMP/GE R7,R4`, `BF 0x0602941E`, with `R7=30`. The routine is repeated heavily during sound-bank initialization, but each individual loop terminates. At 79M the CPU is instead active in the later memory-clear/copy path around `0x0604CCF6..0x0604CCFE`, confirming forward progress between calls.

The same run exposed one missing SH-2 instruction, `0x431B` (`TAS.B @R3`) at `0x060432A2`. `TAS.B` is now implemented with the specified zero test, T flag update, and bit-7 write-back, with smoke coverage for zero and nonzero bytes.

The fixed 80M A/B run reports no unimplemented SH-2 instructions, but its final PC and tail-hot-PC counts are otherwise unchanged. Therefore `TAS.B` was a real CPU-core gap but is not the cause of the `0x0602941E..0x06029422` wait.

Continue beyond 80M using the accelerated bringup command above and classify the next tail before treating it as a blocker.

A 100M run reaches `PC=0x060330D6`, performs about 4.85 million VDP2 VRAM reads, and exposed 16 executions of another missing SH-2 instruction: `0x4n1A` (`LDS Rn,MACL`), first at `0x060361A0` and last at `0x06036DAA`. Direct register-to-MACL loading is now implemented and smoke-covered. A clean CLI rebuild removes those hits and reveals the matching missing `0x4n0A` (`LDS Rn,MACH`) twice at `0x06036DA8`; direct register-to-MACH loading is now implemented and smoke-covered too. Re-run the 100M checkpoint before classifying the `0x060330D6..0x060330DC` tail because the corrected accumulator inputs may affect the path leading there.

Recommended approach:

1. Run beyond 80M and use the tail-hot-PC report plus the retained `0x06029400..0x06029440` post-load probe to distinguish forward-progressing sound initialization from a stable hardware wait.
2. Track whether the game reaches VDP1 command submission or a CD file-transfer request.
3. Keep the SCSP acknowledgement explicit until a real 68k/SCSP execution path exists.
4. Implement the next hardware behavior only when its read/write protocol is proven by the focused probes.

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
