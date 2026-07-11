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

The clean accumulator run proved `0x060330D4..0x060330DC` polls VDP1 `EDSR` bit 1 at `0x25D00010`. The old system map incorrectly routed physical `0x05D00000` into VDP2 VRAM and also placed the surrounding VDP1/VDP2 regions one block too early. The map now separates SCSP `0x05A..0x05B`, VDP1 RAM/framebuffer `0x05C`, VDP1 registers `0x05D`, VDP2 VRAM `0x05E`, VDP2 CRAM `0x05F0..0x05F7`, and VDP2 registers `0x05F8..0x05FB`. The bringup VDP1 register stub reports `EDSR=0x0002`.

With the corrected map, the 100M EDSR probe falls from 12,126,838 hits to 13, final PC advances to `0x0607157C`, and Work RAM High writes rise from about 25.2M to 28.0M. The next missing instruction is `0x0008` (`CLRT`) at `0x0607162C`; it is now implemented and smoke-covered. The next run should rebuild the CLI and classify the `0x0607157A..0x06071580` tail after CLRT is active.

The clean 100M run with `CLRT` active reports no further `CLRT` hits and remains active around `0x0607157A..0x06071580`. It exposes 69 executions of `0x4n05` (`ROTR Rn`), first at `0x0602B680` and last at `0x06071590`. `ROTR` is now implemented with bit 0 copied to both T and bit 31, smoke-covered, and recognized by the CLI decoder. A clean 100M A/B run with `ROTR` active has the same final `PC=0x0607157C`, tail counts, Work RAM High write count, and video-bus traffic. `ROTR` was a real CPU gap but is not the cause of this tail; classify the `0x0607157A..0x06071580` loop with a focused register and memory-read probe next.

The focused gameplay-tail probe proves that `0x0607157A..0x06071580` is not a hardware wait. It is an often-called, finite bit-normalization loop: `SHLL R7`, `ROTCL R6`, `CMP/GE R0,R6`, then `BF/S` with a counter update in the delay slot. Here `R0=0x00100000`, and captured calls show `R6` growing from zero through `1`, `3`, `6`, and onward until the comparison succeeds. The probe records several hot caller PRs (`0x060710C6`, `0x06070FFA`, and `0x06071034`), further confirming repeated calls rather than one stuck invocation. Continue beyond 100M and classify the next phase boundary instead of treating the final sampled PC as a blocker.

The 120M continuation confirms forward progress. Final PC moves to `0x0607166C`, Work RAM High writes rise from `28,004,861` at 100M to `32,973,632`, and the focused trace captures the normalization comparison succeeding at `R6=0x00181C4C` followed by the exit at `0x06071584 -> 0x060715FE`. The hot caller counts continue to grow across the extra 20M instructions. The bringup is executing active game math rather than waiting at the former tail; choose the next slice from missing visible video/hardware behavior rather than another arbitrary final-PC probe.

A core `Vdp1Command` decoder and CLI command-chain inspector now expose the first concrete video target. The 100M final snapshot follows `0x00000 -> 0x10AC0 -> 0x10B20 -> 0x10B00 -> 0x10AE0 -> 0x10B40 -> 0x10B60`. It establishes a `319x223` system/user clip and local coordinate `(160,112)`, then reaches skipped normal-sprite slots and END. This end-of-run snapshot has no active draw primitive, so the next video probe must sample command chains at VBlank or draw start rather than only after the final instruction.

The VBlank command probe samples every accelerated frame, rejects incomplete zero-filled chains, and retains the list with the most visible primitives. In the 90M run the richest chain occurs at instruction 29,000,000 with 11 commands and 8 active normal sprites. It sets system clip `319x223`, local coordinate `(158,107)`, and references sprite data at addresses including `0x118A0`, `0x133A0`, `0x13420`, and `0x13520`, with sizes from `8x8` through `216x16`, before a valid END command. The first renderer slice should therefore implement VDP1 system clip, local coordinate, and normal-sprite drawing before polygon support.

That first software renderer slice is now implemented. `Vdp1SoftwareRenderer` handles system clip, local coordinates, normal-sprite horizontal/vertical flip, transparent pixels, the VDP1 4-bit/8-bit color-bank and LUT modes, direct RGB555, and VDP2 CRAM palette conversion. The CLI option `--dump-vdp1-frame <path.ppm>` renders the richest VBlank snapshot. The 30M NiGHTS checkpoint renders all 8 captured sprites and 1,137 visible pixels; visual inspection of `/tmp/nights_vdp1_30m.ppm` confirms the Sega logo and `SEGA ENTERPRISES, LTD. 1994, 1995` copyright text. The next visual slice can build on this proven path with VDP2 background composition or additional VDP1 primitive types.

WaylandForge live integration initially made the small BIOS copyright glyphs look corrupt even though the Sega logo was recognizable. The active sprites all use `PMOD=0x0028` (16-bit direct RGB). VDP1 two-end-code handling and the direct-RGB transparency threshold are now implemented and smoke-covered, but neither changed the glyph geometry, so the investigation moved to a byte-exact texture comparison.

The follow-up byte-exact comparison closed that suspicion. The CLI now supports `--dump-vdp1-texture`; the complete `216x16` direct-RGB copyright texture at VDP1 byte address `0x118A0` is 6,912 bytes. SystemRegisIII and an instrumented local Mednafen 1.32.1 run both produced SHA-256 `a0e296bd7fba3b27e37a724ed07bd93b6b060a0aa7566d85bb61147eaf3f3ea6`, proving that BIOS decompression and the VDP1 upload are correct. A raw framebuffer comparison then differed at only 758 of 71,680 pixels, mostly from RGB555 expansion, proving the glyph rasterization is also substantially correct.

The remaining live-host failure was presentation state rather than texture data: WaylandForge treated the latest complete VDP1 command list as the entire TV frame, leaving an early black-background logo phase frozen past 145M instructions. `Vdp2BackScreenRenderer` now decodes the solid or per-line back-screen table from VDP2 `BKTA` and VRAM, and `Vdp1SoftwareRenderer` can render sprites over transparent rows. Integer scaling remains selected locally, but scaling was not the root cause.

A captured 29M VDP2 state proved that `BKTA=0` and the black back-screen are intentional while `BGON=0x000F`: the visible white field and large Saturn logo come from the four normal-background tilemaps. `Vdp2TilemapRenderer` now implements the first NBG0-NBG3 cell path for 4bpp/8bpp, one- or two-word pattern names, map/plane/page addressing, character flips, scroll, CRAM offsets, transparency, and priority ordering. The Wayland host composes those live tilemaps with a persistent VDP1 framebuffer instead of treating the current command list as the framebuffer contents. The CLI can preserve a captured checkpoint offline with `--dump-vdp2-state <prefix>`.

The same renderer now covers NBG0/NBG1 bitmap mode at 4bpp and 8bpp palette color, 16-bit palette/direct RGB, and 32-bit direct RGB, including bitmap-size wrapping, scroll, CRAM offsets, and transparency. This closes the next common VDP2 mode switch after the BIOS tilemap phase; rotation backgrounds, line scroll, mosaic, color calculation, and windows remain later slices.

A clean 60M run after the live BIOS screen showed that the transient BIOS `0x000042F8` seen in WaylandForge is not a stable CD wait. The run ends at `PC=0x0602944C`, Work RAM High reaches 12,646,368 writes, and the tail hotspots remain the previously classified finite SCSP bank-initialization loops around `0x0602941E..0x06029422`. CPU/CD bringup is continuing beyond the last visible frame; choose the next checkpoint from missing VDP1/VDP2 presentation behavior rather than treating the black viewport as a CPU stall.

VDP1 solid geometry now covers polygon, polyline, and line commands with local coordinates, system clipping, direct-RGB or CRAM color, mesh pixels, quad triangulation, and Bresenham line rasterization. WaylandForge treats every complete active VDP1 primitive type `0x0..0x7` as a framebuffer update instead of filtering for normal sprites only. Scaled and distorted textured sprites remain the next VDP1 geometry slice.

Scaled and distorted textured sprites are now implemented as textured quads. Scaled sprites decode VDP1 zoom-point modes and display width/height or alternate coordinates; distorted sprites use all four command vertices. Both paths support local coordinates, system clipping, horizontal/vertical texture flip, mesh, transparent texels, the existing palette/LUT/direct-RGB fetch modes, and affine barycentric texture mapping across the two quad triangles. Focused smoke cases cover a scaled direct-RGB sprite and a skewed distorted sprite.

A final-state VDP2 capture at 120M instructions rules out an unimplemented active background mode at that checkpoint. The master is still advancing in game code at `PC=0x060712D6`, Work RAM High has 26,528,574 writes, and both VDP2 VRAM and CRAM differ from the 60M capture, but `TVMD=0x8000` and `BGON=0x0000`: the program has deliberately left every VDP2 background disabled after loading new video data. The CLI now accepts `--dump-final-vdp2-state <prefix>` so this end state can be preserved even when the richest earlier frame is different.

WaylandForge advances 10,000 master instructions per UI frame but raises Saturn VBlank every 100,000 instructions, so its hardware cadence already matches the diagnostic runs. A 60M run with a deliberately altered 10,000-instruction VBlank interval produced byte-identical final VDP2 registers, VRAM, and CRAM, ruling cadence out as the black-screen cause. Forcing CD status to Pause is also wrong for this path: it returns to the BIOS response loop at `0x000042EC`, while Standby reaches NiGHTS code and performs substantially more initialization. The hot Standby PC at `0x06048FF2` is not a hardware wait; a runtime Work RAM dump decodes it as the finite three-instruction loop `ADD #1,R4`, `CMP/GE R5,R4`, `BF 0x06048FF2`. Use `--dump-final-wram-high <path.bin>` to preserve exact relocated/decompressed runtime code for the next focused probe. At the current interpreter rate, the UI's 56.5M instruction counter represents only a few seconds of original Saturn CPU work even though reaching it takes minutes of wall time; prioritize core throughput or a safe warm-state mechanism rather than changing UI/VBlank batching.

The first throughput slice removes redundant page resolution from ordinary word/long bus accesses and delegates non-internal SH-2 word/long accesses directly through the internal-register wrapper. CLI instruction tracing is now genuinely opt-in instead of formatting and retaining a trace event on every instruction when `--trace` is absent; WaylandForge likewise runs the Saturn core without an unused per-instruction trace sink. A warm Release 60M NiGHTS checkpoint completes in 54.31 seconds instead of roughly 3.5 minutes. It ends at the same `PC=0x0602944C` with the same 12,646,368 Work RAM High writes, and final VDP2 register/VRAM/CRAM hashes are byte-identical to the pre-optimization baseline.

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
