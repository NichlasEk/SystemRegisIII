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

Longer checkpoints prove that simply waiting is no longer useful: final VDP2 registers, VRAM, and CRAM at 120M and 240M are byte-identical with `BGON=0`, even though Work RAM High writes grow from 26.5M to 56.3M. The hot `0x06071560..0x060715A0` math path begins around 63.5M and is repeatedly called mainly from `0x060710C6`, `0x06070FFA`, and `0x06071034`. A 500,000-instruction VBlank A/B substantially changes CPU work and CD status-poll counts but still produces the same VDP2 hashes, ruling out the accelerated 100,000-instruction cadence as the display blocker.

The CD drive model now follows the locally checked Mednafen reference for command `0x04`: Initialize reports Busy and transitions to Pause after a short bringup delay instead of remaining in Standby forever. Focused smoke coverage verifies Busy and Pause responses. NiGHTS reaches the Pause status, but its 120M command history and VDP2 hashes remain unchanged, so this is a fidelity correction rather than the black-screen fix. The next useful slice is a differential SH-2 trace against the local Mednafen build around the first post-BIOS execution of the `0x06070xxx` math path, looking for the first register or memory divergence rather than another arbitrary instruction checkpoint.

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

## July 11 Differential Bootstrap Checkpoint

The earlier apparent game path was not executing the disc initial program. The NiGHTS ISO root executable is `0NIGHTS` (447,900 bytes) with entry address `0x06004000`; the old run first reached `0x06010000` without that file anywhere in Work RAM High. The explicit diagnostic option `--simulate-initial-program-load` now loads the disc IP sectors at `0x06002000`, loads the first root file at its IP-header entry address, and applies the master SH-2 entry state captured from a local Mednafen run. This remains a diagnostic bridge and is not enabled by default or in WaylandForge.

The CLI can emit an architectural trace with `--dump-sh2-diff-trace <path>` and `--sh2-diff-trace-count <N>`. It can also preserve both WRAM banks at the bridge with `--dump-initial-wram-low` and `--dump-initial-wram-high`. A 100,000-state comparison proved the early clear/copy path and subsequent BIOS service flow agree structurally with Mednafen once SMPC status-flag latency is modeled. The SMPC SF bit now remains busy for two BIOS-visible polls after a command instead of clearing synchronously.

SH-2 opcode `0x001B` (`SLEEP`) is now implemented. The CPU stops fetching at the post-instruction resume PC, masked interrupts leave it asleep, and an accepted interrupt wakes and vectors it. Focused smoke coverage verifies all three cases.

The direct bridge still returns to BIOS `SLEEP` at `0x0000052E`/resume `0x00000530`. This is not an unimplemented-opcode spin. Mednafen reaches the same sleep with `EPending=0` when forced through the equivalent captured entry, and does not wake during a 35-second reference probe. A byte-exact entry snapshot comparison showed Work RAM Low identical; Work RAM High was identical from `0x06004000` upward. Loading the four IP sectors reduced the remaining High-RAM difference to 1,725 bytes in `0x06000000..0x06001FFF` plus 29 BIOS-patched bytes in the IP area, but a full reference-WRAM diagnostic still returned to the same sleep. RAM content is therefore not the remaining cause.

The next slice should replace the direct executable handoff with the real Saturn CD boot transaction/state transition. Trace the BIOS path that is supposed to transfer the IP and initial program, including CD HIRQ/data-transfer state and the SMPC/CD-on relationship, and stop at the first device-state divergence. Do not enable the explicit bridge in WaylandForge until that path reaches sustained game code or submits a post-logo VDP frame.

The first real-boot CDB differential is now instrumented. Summary output includes a `CD command timeline` with instruction index, master PC, command, and all four command registers. `--sh2-diff-trace-trigger <hex>` allows the architectural trace to arm at an arbitrary BIOS or game address instead of only `0x06004030`.

The local Mednafen command sequence begins `01, 75, 06, 01, 67, 48, 60, 02, 06, 03, 03, E0, E1, ...`; the old SystemRegis path issued only `01`. At BIOS command completion around `0x000042C0`, Mednafen has already cleared CMOK and reports HIRQ `0x0BE0`. The actual pre-command ready state is CMOK, while the remaining `0x0BE0` startup bits become observable as the first command enters the CDB. SystemRegis now models that split instead of exposing a constant reset HIRQ.

For a valid Saturn disc, the first hardware-info command now matches the reference at instruction `8,777,893`: command `0x01`, CR input `0100,0000,0000,0000`, response `0000,0002,0000,0600`, and startup completion raises CMOK plus EFLS. The drive then transitions from Busy to Pause. This removes the previous incorrect Standby hardware-info response. The BIOS has not yet emitted reference command `0x75`, so the next blocker is the asynchronous filesystem-startup/EFLS acknowledgement after this matched first command, not the initial hardware-info payload.

The asynchronous startup transition is now modeled as well. Mednafen keeps the Hardware Info Busy response visible long enough for the BIOS to consume it, then publishes recurring periodic Pause reports (`CR=2180,4101,0100,0096`) and raises SCDQ. The BIOS acknowledges successive SCDQ events with `HIRQ=0xFBFF`; SystemRegis now reproduces that sequence instead of leaving the original Busy CR values visible forever. A 10M instruction run without a forced `--cd-status` drops CD-register reads from hundreds of thousands to 488 and advances into disc program code around `0x06001656`.

Do not force `--cd-status standby` for the real boot path. That legacy override changes the first response to `CR1=0x0200` and recreates the BIOS response loop. WaylandForge no longer supplies `MountedDiscInitialStatus=Standby`, so its ROM-picker launch uses the verified automatic Busy-to-Pause startup. The next differential target is the later CDB command stream: SystemRegis has escaped the first response loop but still does not yet reproduce Mednafen's immediate `0x75, 0x06, 0x01, ...` sequence.

The local Mednafen instruction counter then corrected the startup timing model. Its first `0x01` arrives at master instruction 17,041,446, periodic Busy reports begin immediately afterward as `2000,FFFF,FFFF,FFFF`, Pause reports begin around 33.7M as `2100,4101,0100,0096`, and `0x75` arrives at 142,695,636. Scaled to SystemRegis's first-command point, the CD register device now advances from explicit master-instruction ticks, raises recurring SCDQ roughly every 200,000 instructions, and changes Busy to Pause after 8.6M instructions instead of coupling device time to CR4 reads. Both the CLI and WaylandForge feed that clock.

The black live run at `PC=0x06013244..0x06013248` was a separate, proven SCU DSP wait. The loop reads the DSP Program Control Port at `0x25FE0080`; SystemRegis kept execute value `0x00018000` forever because no DSP engine completed it. The SCU bringup device now clears the execute bit after 16 complete status polls, with focused smoke coverage. A clean Release 80M run leaves that loop, ends at `PC=0x0602E89E`, and raises Work RAM High writes from 3,503,547 to 23,411,717. The later reference CD command sequence is still absent, but the former `0x06013244` live black-screen stall is removed.

The 160M continuation reaches the next major milestone at instruction 93,650,551: SystemRegis now emits the reference prefix `01,75,06,01,67,48,60,02`, including the required `HIRQ=0xFDFE` acknowledgement before `0x75`. After Get TOC, however, it reads the CDB data port far beyond the 204-word TOC and eventually executes zero-filled Work RAM at `0x06010E48`. A snapshot proves the corresponding Mednafen region contains NiGHTS code while SystemRegis still contains zeros. Loading the whole executable at first command, at Pause, or at EFLS completion was tested and rejected: each timing either gets cleared later or supplies bytes without the incremental FLS CPU/RAM state, leading to invalid pointers. Do not enable any of those shortcuts in WaylandForge.

The next slice must model the incremental FLS/CD-to-Work-RAM transfer that occurs during the long Busy authentication phase. Capture the Mednafen destination writes and sector/FAD progression between command `0x01` and the `0xFDFE` completion, then reproduce those writes in order. The isolated valid CPU gaps found during this investigation are now covered: `SETT` (`0x0018`) and `MAC.W` (`0x4nmF`). The SMPC address window is mirrored through `0x0017FFFF`, preventing valid `0x00101xxx` aliases from faulting.

## July 11 Host Data Port Correction

The first post-freeze revalidation found that the CD host-data FIFO was decoded at the wrong address. The BIOS reads words from `0x25898000`, while the register device and all smoke coverage used `0x25890000`. The FIFO is now mapped at the BIOS-observed address, all TOC/sector/file-transfer smoke cases use that port, and CLI stub diagnostics report the active transfer plus consumed/total word counts.

The next reference differential exposed an additional address-decode detail: the Saturn CD registers repeat throughout CS2. The reference BIOS reads the host-data mirror at `0x25818000` as well as the later `0x25898000` mirror. Mednafen's decode is `(address & 0x7FFF) < 0x1000` with the register selected by `address & 0x3F`. The clean-room device now applies that mirror decode to the FIFO and all command registers, with smoke coverage that begins a TOC transfer through `0x25818000` and continues it through `0x25898000`. The earlier 30M/95M success measurements used a forced drive-status override; see the automatic-boot recheck below.

Before the mirror fix, the corrected 94M accelerated NiGHTS run consumed exactly `204/204` TOC words but still reached the first zero opcode at `0x06010E48`. That established that the old description of an unbounded TOC read was only a symptom of the invisible FIFO. The later reference CPU-context probe identified the earlier `0x25818000` host-port mirror, but the automatic-boot recheck below proves that correct mirroring alone does not supply the missing stream.

## July 11 Automatic-Boot Recheck

The apparent 95M success at `0x06028F9A` used the legacy `--cd-status standby` override and is not the verified automatic boot path. Rechecking without that override still reaches the first zero opcode at `0x06010E48` after the command prefix `01,75,06,01,67,48,60,02` and exactly `204/204` TOC words. A Mednafen A-bus source probe confirms that the reference master SH-2 receives the missing longwords from `0x25818000`; for example, the read values `D20C6021`, `20088945`, and `2F462F06` are written into `0x06010E00..0x06010E4B`. The remaining gap is therefore the host-transfer contents/state, not address mirroring alone.

The CUE reader now preserves the complete multi-file track table instead of advertising only the first data track. It computes every track FAD from the file lengths and `INDEX 01` offsets, preserves data/audio control bits, and reports the physical leadout. NiGHTS therefore exposes the same 21-track shape as the reference rather than a synthetic single-track TOC. A 105M automatic run proves this fidelity correction alone does not fill `0x06010E48`; the next slice must identify which reference buffer/transfer owns the non-TOC stream read through `0x25818000` and start that stream at the matching command/state boundary.

That buffer owner is now identified. In Mednafen, the missing word `D20C` is host-transfer word 26,374 from buffer `0x19`, at buffer-list position 25 of a 200-sector transfer. The owning command sequence is `70` Change Directory, `72` Get Filesystem Scope, `74` Read File for file ID 2, status/file-info queries, then `63` Get-and-Delete Sector Data with `CR4=00C8` (200 sectors). The `0x25818000` stream is therefore the initial executable file transfer, not TOC or an implicit authentication stream. SystemRegis currently stops after command `02` and never reaches that filesystem/read-file sequence. The immediate differential is the post-TOC completion/return path that should issue command `06`, followed by filesystem commands; do not prime an executable FIFO at command `01`.

The CLI now supports `--dump-pre-unimplemented-trace <path>`, a bounded 256-state architectural ring that arms near the end of the 204-word TOC and freezes at the first unknown opcode. It proves the immediate control transfer is deliberate rather than a corrupt return: at `0x0600091E` the BIOS interrupt/callback handler loads `R6=0x06010E48`, then `JSR @R6` at `0x06000924` with return address `0x06000928`. The callback slot points into the not-yet-loaded initial executable. The next reference probe must capture this same handler invocation and callback pointer timing; the remaining question is why SystemRegis publishes/dispatches that callback before the `70,72,74,...,63` file load has populated its target.

## July 11 DTREQ Status Correction

The pre-fault trace exposed the actual TOC overrun. Mednafen returns `CR1=0x4000` for a host-data transfer, but SystemRegis returned `0x4080`. The BIOS packs the low byte of CR1 above CR2, turning the expected length `0x000000CC` into `0x008000CC`; its copy loop therefore ran for roughly eight million words and eventually dispatched a callback into overwritten/empty memory. `StartDataTransfer` now returns the reference `0x4000`, with all TOC, sector, file-info, and CUE smoke expectations corrected.

A clean automatic 105M run now consumes the 204-word TOC, immediately issues `06`, and continues through the reference sequence `03,03,E0,E1` without any unimplemented opcode at `0x06010E48`. A 160M continuation still has no first-unimplemented fault and reaches `PC=0x06071672`, with 50,155,599 high-WRAM writes. It later falls into repeated command `00` status polling rather than reaching `70,72,74,63`; the next differential is authentication completion/status behavior after `E1`, not TOC transfer length or the former callback crash.

The E0/E1 response differential is now corrected as well. Mednafen returns E0 as `0100,4101,0100,0096` at Pause without the periodic/CD-ROM low-byte flags, raises HIRQ from `0x0DC0` to `0x0FC4` through EFLS (`0x0200`), SCDQ (`0x0400`), and CSCT (`0x0004`), then returns E1 as `0000,0004,0000,0000`. SystemRegis now matches that response shape and completion-bit set, with smoke coverage. A 130M automatic run still transitions to repeated command `00` polling rather than reference command `70`; the next divergence is therefore above the raw E0/E1 result and HIRQ values, likely the BIOS authentication-completion callback/state that chooses the filesystem bootstrap path.

`--dump-post-auth-trace <path>` now captures 1,024 architectural states immediately after command E1, and `--dump-final-wram-low` complements the existing high-RAM dump. The first trace shows the BIOS command helper observes `HIRQ=0x0FC5`, reads the correct E1 payload (`CR1=0000`, `CR2=0004`), and then enters the response-table comparison routine at `0x00003BFC..0x00003C44`. SystemRegis fails to select the reference continuation even though the device-visible values match; the next probe should compare the response-table/stack operands in that routine against Mednafen, with special attention to the response descriptor in high WRAM around `0x06001EF4`.

The focused operand recheck falsifies that last response-table hypothesis. At `0x00003C16..0x00003C24`, the actual descriptor at `0x06001EF4` and the stack candidate at `0x06001EC8` both compare equal as `00000004,00000000`; the matcher returns through `0x00003C40`. The HIRQ timeline also proves the BIOS acknowledges CMOK immediately after E1 with `FFFE`, changing `0x0FC5` to `0x0FC4`. The divergence is therefore in the higher-level post-auth callback/state after a successful E1 match, not raw response data, HIRQ clearing, or the table matcher. `--post-auth-trace-count N` now extends the existing post-auth trace beyond its 1,024-state default so that caller path can be captured without broad always-on tracing.

The instruction-level Mednafen differential found the missing asynchronous transition. About 2,000 executed instructions after E1, the reference replaces the one-shot `0000,0004,0000,0000` response with periodic Busy status `2000,4101,0100,0096`. That lets BIOS handler-table entry `0xDB` match and issue Change Directory. The CD device now schedules the same post-auth report while preserving the immediate E1 response. Smoke coverage checks both sides of the 2,000-instruction boundary. A clean automatic 95M NiGHTS run consequently issues `E1` and then reference-shaped command `70` at instruction 93,671,210 with `CR=7000,0000,17FF,FFFF`. The immediate reference sequence is in fact `70,75,04,30`; the earlier expectation of direct `72,74` omitted this initialization phase.

Change Directory and Abort File now return the reference one-shot position `0000,4101,0100,0096`. Abort File then schedules the same delayed periodic Busy report, allowing BIOS to continue to Initialize. Initialize returns `0000,4101,0100,00A6`, including the observed 16-sector FAD advance, and command `30` preserves that position with zero status. The CLI command timeline now includes result CR words and HIRQ alongside every command input. The next differential begins after command `30`, with this initialization prefix matched.

A 94M automatic recheck confirms command `30` now returns exactly `0000,4101,0100,00A6`, matching Mednafen, but SystemRegis remains in the BIOS post-command path around `0x00003320` instead of issuing the reference `03,03,10,51` continuation. Letting the old startup-periodic timer continue or stopping it at Initialize does not change that boundary. The next probe should therefore compare command-30 completion/event timing and the `0x00003320` wait path, not alter the already matched response words.

The command-30 execution differential identified the missing event precisely. Mednafen completes command `30` after eight HIRQ word polls with both `CMOK` and `ESEL`; at BIOS `0x00004286` its poll counter is `R7=7` and HIRQ is `0x0FC5`. SystemRegis previously completed immediately with only CMOK, leaving HIRQ `0x0F85` and the BIOS polling ESEL forever at `0x00003320`. The CD device now delays completion for the same eight polls and raises `CMOK|ESEL`, with smoke coverage. `--dump-post-command30-trace` captures a focused 512-state architectural trace for this boundary.

A clean automatic run now leaves the wait and issues both reference Session Info commands at instructions 93,674,453 and 93,674,692. Their results match Mednafen as `0000,0000,0100,4CFE` and `0000,0000,0100,0000`; Session 1 no longer incorrectly returns FAD `0096`, and non-periodic session status no longer carries the CD-ROM low flag. At 93.690M the BIOS is still scanning the response path around `0x00004C50` and has not yet issued reference command `10`. The next differential starts at that `03,03 -> 10` handler selection.

The extended Mednafen execution probe shows that the second Session Info response is later replaced asynchronously by `0100,00A6,0100,0000`; handler-table entry `0x4E4` then matches and builds command `10`. SystemRegis now publishes the same two-phase post-session report after 11,000 master instructions, with smoke coverage for the boundary. A 93.720M run confirms the device-visible CR words are correct but the BIOS remains in `0x00003BF8..0x00003C3C` and has not issued `10`. The next trace must capture the SystemRegis response descriptor and table index after that delayed publication and compare them at the reference `0x4E4` match; do not retune the delay without that evidence.

`--post-command30-trace-count N` now extends the focused command-30 trace beyond 512 states. A 50k-state run reached only table index `0x150`, confirming that SystemRegis executes substantially more architectural instructions per table entry than Mednafen's fetch-level probe reports. Extending the run to 94M still did not issue `10`. Enabling `--defer-vblank-in-critical-windows` produced millions of deferrals without changing the result, so VBlank interruption is not the cause. Compare the packed descriptor and stack candidate when SystemRegis actually reaches index `0x4E4`; the device-visible delayed CR words already match.

A 250k-state trace reached and passed index `0x4E4`. Both compared longwords match exactly (`010000A6,01000000`), so the descriptor and table data are not divergent. The remaining architectural difference at the successful comparison is the BIOS command-completion poll count: Mednafen carries `R7=7`, while SystemRegis carried `R7=0` because Session Info completed immediately. Commands `03` and `30` now share the reference eight-word-poll CMOK delay. Smoke coverage remains green; the next long run must verify that this poll-count correction produces command `10`.

The clean 94M acceptance run falsifies poll count as the final cause: command history still ends in `03,03`, although the focused 260k-state trace now reaches entry `0x4E4` with `R7=7`, HIRQ `0x0FC5`, and the matching operands `010000A6,01000000`. The successful comparison calls the BIOS response packer at `0x00004C50`, which copies the response into the working candidate and returns to the table loop; SystemRegis then continues scanning through at least entry `0x78B` without issuing `10`. The next Mednafen differential must compare the working buffer and registers immediately after `0x00004C50` returns at the `0x4E4` match, especially the byte tested at `0x00003BA6..0x00003BB8`. Do not further retune command `03` timing or the already matched CR/HIRQ values without new reference evidence.

The focused Mednafen execution probe found the next concrete mismatch. At entry `0x4E4`, the reference response packer observes control byte `0x24`; SystemRegis observes `0x01`. The subsequent `0x20` test therefore exits the table scan in Mednafen and continues to command `10`, while SystemRegis keeps scanning. An intermediate correction moved the delayed post-session report from the earlier inferred `0100,00A6,0100,0000` to the ordinary Pause layout `0100,4101,0100,00A6`. A new BIOS response-stack watch covers `0x06001EBC..0x06001EDF`. That layout made the matcher reach `0x4E4` on operand `010000A6`, but its callback control byte remained `0x01` and the 94M command history still ended in `03,03`, motivating the cache-aware provenance probe below.

The cache-aware Mednafen probe resolved that control byte without a shortcut. BIOS `0x000042EC` copies CDB CR1 directly to `0x06001EC4`; at the successful reference match the eight-byte descriptor is `24 00 41 01 01 00 00 A6`. The delayed report is therefore periodic Seek status `2400,4101,0100,00A6`, not Pause `0100,4101,0100,00A6`. Publishing that exact report makes the clean 94M SystemRegis run issue Play Disc command `10` at instruction 93,686,203 with the reference input `1080,0096,0080,0010`. Command `10` now also returns the reference one-shot result `0000,4101,0100,00A6`, with smoke coverage. A 94.5M continuation has not yet issued the reference command `51`; the next differential begins after the now-matched Play Disc completion, not in the resolved `03,03 -> 10` path.

The post-Play differential found the asynchronous end transition. After BIOS acknowledges CMOK, Mednafen raises `PEND` (`HIRQ 0x0FC4 -> 0x0FD4`) while the drive enters Pause; its subsequently readable report is periodic Pause `2100,4101,0100,00A6`. The CD device now schedules that transition from the 16-sector Play request. A clean 95M run consequently advances from command `10` to `51`, `63`, and `06` at instructions 93,750,651, 93,750,930, and 93,792,189. Command `51` initially exposed the next mismatch by inheriting the old status and making `63` request `0x0096` sectors; it now returns the reference-shaped sector count `0100,0000,0000,0010`. The acceptance rerun confirms `63` receives `CR4=0010`; its initial result still differed from the earlier Mednafen capture (`2180,4101,0100,0096` versus `4180,4101,0100,00A6`), leading to the transfer fix below.

The `63` mismatch came from a missing buffer side effect, not only response formatting: Play Disc did not populate the connected partition, so Get-and-Delete Sector Data took the empty-partition status path and BIOS read zeros. Play now exposes its 16-sector range through partition 0, and `63` returns `4180,4101,0100,00A6` while starting the real FIFO. A second host-port bug then explained why End Data Transfer reported only `0x2000`: 32-bit reads consumed the first data-port word but treated bytes 2-3 as another register. The four-byte port window now consumes two words per longword. The clean 95M acceptance run matches Mednafen through `63` and `06`, including `0100,4000,0000,0000` after all 16,384 words; the earlier TOC end-transfer response also improves from periodic `2100,00CC` to the reference `0100,00CC`. The next differential begins after this matched End Data Transfer completion.

The post-transfer periodic Pause report now releases the BIOS into the real filesystem bootstrap. SystemRegis reaches the reference commands `70,72,74` at instructions 93,798,237, 93,798,522, and 93,800,831. Root Change Directory returns `0180,4101,0100,00A6`; Get Filesystem Scope first reports WAIT as `0300,00B0,0100,0002`, then completes asynchronously; Read File starts as `0080,4101,0100,00AF` with `HIRQ=0x0DD4`. The modeled 200-sector fill then publishes periodic Pause at FAD `017D` and raises deferred EFLS, matching the Mednafen event ordering. A 96M run still remains in the BIOS response-table scan after that deferred event and the richest VDP1 capture remains the same 11-command, 8-sprite Sega/copyright frame. The next differential should inspect the response descriptor/control byte after deferred Read File EFLS; the expected continuation is `00,00,73,06,51,63` and the 200-sector executable transfer.

## July 19 Read File Stream Continuation

The apparent bad `BSRF` target at `0x060681EE` was valid SH-2 behavior but exposed corrupt executable contents. A Mednafen execution probe and a direct ISO read agree that `0NIGHTS` offset `0x641EC` must be `00 09 D3 47 60 31 00 0B`. SystemRegis instead placed file offset `0x0001EC` at RAM offset `0x641EC`. The BIOS write provenance showed that the second `63` Get-and-Delete Sector Data command requested another 200 sectors from the unchanged partition start after the first 200-sector transfer.

Read File partitions now retain the complete file-stream end FAD. A Get-and-Delete transfer advances the partition FAD and refills its 200-sector window from the remaining file tail. This behavior is deliberately limited to active Read File streams so the already matched 16-sector Play Disc bootstrap remains unchanged. Smoke coverage uses a 201-sector ISO and verifies a 200-sector transfer followed by a one-sector refill.

The automatic NiGHTS acceptance now issues the second `63` with `CR4=0013` rather than `00C8`. At `i=122,418,378`, `PC=060040A6` loads `R0=060069CE`, and Work RAM High at `060681EC` is byte-identical to the ISO and Mednafen: `00 09 D3 47 60 31 00 0B 60 0D E0 01 D5 45 D1 44 ...`. Do not use explicit `--cd-status standby` for this comparison; it bypasses automatic authentication startup and follows a different BIOS path.

The former recursive stack failure is gone, but the next acceptance reaches a new bus fault at `PC=06068564` with `R15=06001FA0` and bad data address `F9F8F36B`. Before that fault, the game writes `00000400` over raw Work RAM address `060681EE`; subsequent instruction fetches report unimplemented `0000` in the same region even though the originally loaded code was correct. This is strong evidence that the next differential is SH-2 instruction-cache behavior or cache invalidation, not CD data, DMA, or branch semantics. The CLI now records SH-2 DMA history and watches `060681C0..0606821F`; only the two expected channel-1 transfers occurred, neither targeting this code region.

## July 19 SH-2 Unified Cache Checkpoint

The cache hypothesis is now confirmed. `Sh2InternalRegisterBus` models the SH7095 CPU-local unified cache as 64 sets, four 16-byte ways, with CCR at `FFFFFE92`. CE enables cacheable P0 accesses, ID/OD suppress instruction/data replacement, TW selects the reduced-way mode, CP invalidates every line and self-clears, and cache-hit CPU writes remain write-through to external memory. Master and slave caches are separate; on-chip DMA still bypasses them. `Sh2Cpu` now uses a distinct instruction-fetch interface, including delay-slot execution through the normal step path. Smoke coverage verifies line fill, stale data after another CPU writes external RAM, cache-hit write-through updates, purge, master/slave isolation, and an actual CPU opcode fetch that remains cached after the slave changes raw RAM.

The automatic 126M NiGHTS acceptance has `CCR=01`, about 395M byte hits and 3.08M line misses. It passes the old `06068564` fault with no unknown opcode and reaches `PC=0606DB50`; raw WRAM at `060681E0..0606857F` has already been cleared, proving execution is coming from the CPU-local cache rather than accidentally preserved backing memory. A 135M continuation remains fault-free and repeatedly executes the intact cached opcode `D347` at `060681EE`.

The same continuation exposed and partially corrected the next CDB state mismatch. Deleting Read File sectors now advances `_currentFad`; after the 19-sector tail, Abort/Initialize preserve reference FAD `0190` and return `0080,4101,0100,0190`. The late Initialize raises SCDQ, Set Sector Length returns the same position, and SystemRegis now advances from command `60` to the reference command `00` at instruction 122,420,217. It does not yet issue the following reference command `06`; the next differential is the BIOS handler/completion state for this matched `00` result, not cache correctness or executable contents.

The missing completion was the asynchronous periodic drive report between commands, not the immediate `00` response. Abort File's existing 2,000-instruction deferred slot now distinguishes the late file-stream position from early authentication: it settles the drive into periodic Pause status `2180,4101,0100,0190` and raises SCDQ without resetting the pickup to FAD `0096`. Smoke coverage reproduces the late `75,04,60,00` sequence and verifies both sides of the timer boundary. The automatic 124M NiGHTS acceptance consequently advances through the reference-shaped sequence `00,06,02,06,03,03,48,03,03,44` beginning at instruction 122,420,217. The next blocker is later and separate: after command `44`, the master reaches BIOS response handling with corrupted `R15=FFFFFF92` and faults on data address `F9FFE123` at `PC=06001F2E`. Start the next trace at the post-`44` response/return path; do not retune the now-proven SCDQ report.

The post-`44` trace proved that the apparent stack corruption was BIOS executing its response workspace as code after an unimplemented command left the preceding periodic `2180` result in place. Set Filter Subheader Conditions (`42`), Set Filter Mode (`44`), and Set Filter Connection (`46`) now validate their partition, publish the ordinary non-periodic drive report, and raise ESEL. The 124M acceptance matches the reference continuation through `44,42,46,00,48,51,00,00,50,48,30,00,00,10,51`; the first fault is gone. After that Play request, SystemRegis repeatedly returns stale Pause/FAD `0180,...,0190` to command `00` and eventually enters response data again. The next differential is the new Play request's asynchronous drive/FAD transition (the reference moves toward FAD `00A6`), not SH-2 stack handling or filter completion.

The late Play request now preserves `0190` in its immediate response, starts the pickup at FAD `00A6`, cancels the stale Initialize transition, and delays partition sectors until the seek completes. Get Sector Number carries the current drive status instead of hardcoding Pause. Long seeks use a separate Busy-to-Seek transition before their end notification, while the proven short 16-sector bootstrap retains its fast timing. Smoke covers the old position response, zero sectors during seek, Seek status `0400`, end FAD `00A7`, PEND, and publication of the completed sector. The automatic 124M run is bus-fault-free and matches the reference polling structure with 522 command-`51` calls and 1,043 command-`00` calls before reaching Seek. The new boundary is an unimplemented-zero stream beginning at `060508F0` and ending at `PC=060515E4`; trace the cache/raw-WRAM provenance of that return region rather than changing the now-matched CDB responses.

## July 19 Slave Identity and Cache-Control Spaces

The zero stream was not a missing opcode or a CD overwrite. A master-side watch proved that `060508F0` had already executed hundreds of times with opcode `2F36`, while a slave-side watch caught the later destructive writes: slave BIOS loop `PC=000002B0` wrote zero across the same region. The slave had incorrectly followed the master BIOS path because its role marker was stored at `FFFF FFE0` as `20000000`; SH7095 BCR1 actually begins at `FFFF FFE2`, with the read-only MASTER bit at bit 15. BCR1 now resets to `03F0` for master and `83F0` for slave, and writes preserve the role bit.

The same provenance pass found a separate memory-map error. `60000000..7FFFFFFF` is the SH7095 cache address-array space, not a high-WRAM mirror. The CPU-local bus now implements address-array longword access, `C0000000..C0000FFF` cache data-array access, `40000000..47FFFFFF` associative purge writes, and the documented six-bit pseudo-LRU replacement order. Removing the false `60000000` WRAM mapping eliminates 2,048 spurious high-WRAM writes in the 124M run. Smoke covers BCR1 identity/write protection, address/data arrays, associative purge, cache fill, write-through, full purge, and master/slave isolation.

The corrected automatic 124M acceptance retains the original bytes at `060508F0` in raw WRAM, records zero slave writes to `060508D0..0605094F`, has no unimplemented opcode or bus fault, and reaches valid master/slave game PCs `0606E15A` and `06005FA0`. At 126M both CPUs remain fault-free at `0606E70A` and `06005F9E`; the CD sequence has completed the long seek and advanced to its first command `52` with `CR=5200,0000,0000,0001` and response `2180,4101,0100,00A7`. The next differential starts at command `52` handling after the now-correct slave startup and cache-control behavior, not at the former `060508F0` zero stream.

## July 19 CD Actual-Size and Sector-Transfer Continuation

The local Mednafen differential identifies `52` as Calculate Actual Size and `53` as Get Actual Size. For NiGHTS's one buffered 2048-byte sector, the reference retains Seek position `0480,4101,0100,00A7`, raises ESEL after the calculation, and reports `0400,0400,0000,0000`: 1,024 16-bit words. Command `61` then returns `4480,4101,0100,00A7` while exposing those 1,024 words through the host FIFO.

The CD block now implements partition-aware commands `51`, `52`, and `53`, actual-size calculation for all four configured sector lengths, delayed ESEL completion, and the reference Seek/DTREQ response for `61`. Long Play completion remains in Seek at FAD `00A7`; the earlier 16-sector bootstrap still completes in periodic Pause. Smoke coverage exercises the complete late-play sequence and verifies the delayed interrupt, actual-size result, position words, and FIFO length.

The clean automatic 126M acceptance has no command fallback at `52`. It advances through `52,53,61,06,62`, ends with the host transfer inactive, and remains fault-free at valid game PCs `0606B35A`/`06005F9E`. CDB command counts contain one each of `52`, `53`, `61`, and `62`; the recent tail is `51,52,00,00,53,61,00,06,62`. The next differential begins after Delete Sector Data command `62`, whose final visible response is periodic Seek `2480,4101,0100,00A7`.

## July 19 Post-Delete Drive Phases and CMP/STR

The reference does not raise PEND when the first long-seek sector merely becomes available. After `62` removes that sector, it reports Seek for 12 status commands, transitions to Busy with SCDQ for 189 reports, and only then raises PEND and enters Pause. Command `50` reports `0100,00C8,1800,00C8`. SystemRegis now models that partition deletion and drive-phase sequence; smoke covers every boundary and preserves the earlier short 16-sector bootstrap behavior.

The next 127M acceptance reached a genuine missing CPU instruction: opcode `264C` at `06050A86` is SH-2 `CMP/STR R4,R6`, not corrupt code. `CMP/STR` now compares all four corresponding byte positions and sets T when any pair matches, with positive and negative smoke cases. The rerun has no unimplemented opcode or bus fault and completes six `52,53,61,62` cycles.

The subsequent five-sector Play request needs its end state tied to partition draining rather than the old fixed 5,000-instruction timer. The device now advances the buffered FAD as sectors are deleted, reports Play while more than one sector remains, switches to Busy at the reference final-sector boundary, and keeps the final reset-selector/status transition non-periodic before Pause. A clean 128M run remains fault-free at valid master/slave PCs `0606F252`/`06005FA2`, with six actual-size/sector/delete cycles completed. Its current live tail is repeated command `00` with `0080,4101,0100,00AF`; the next differential is the final Busy-to-Pause/event timing that should release the following `48,44,42,46` sequence, not `CMP/STR` or the resolved first `62` transition.

## July 20 Final-Sector HIRQ Completion Edge

The repeated Busy tail contained an earlier command-completion timeout hidden inside the BIOS polling path. Mednafen exposes final Delete Sector Data as `62 -> 0080,4101,0100,00AF` with HIRQ `0B44`, then publishes `CMOK|EHST` before the following `00 -> 0080,4101,0100,00AF` at HIRQ `0BC4`. SystemRegis previously left EHST asserted on command `62` and either exposed CMOK too early or withheld it permanently.

Final deletion now preserves CSCT while withholding CMOK and EHST, then releases CMOK and EHST on the first HIRQ poll. Smoke coverage verifies both sides of that edge. A clean 126.8M trace matches `62=0B44` at instruction 126,666,766 and the next `00=0BC4` at 126,667,265; the master no longer ends in the `06068398..060683AC` CMOK timeout. CLI summaries retain the full final SH-2 register state plus bounded recent command and HIRQ timelines for this differential.

The remaining tail repeatedly issues command `00` with Busy/FAD `00AF`. The reference issues `51` with a zero sector count after its first final status report and then proceeds through `48,44,42,46`. An attempted deferred PEND assertion only changed the repeated HIRQ state to `0BD4` and was removed. Continue by comparing the BIOS acknowledgement/task scheduling between the matched final `00` and the missing `51`; preserve the now-verified `62 -> 00` completion ordering.

## July 20 Final-Drain Periodic Report

A deterministic `--sh2-diff-trace-cd-command` plus `--sh2-diff-trace-cd-occurrence` trigger now arms the architectural trace on a selected CD-command instance. Together with a focused `060662E0..060662EF` watcher, it shows BIOS routine `0606F240..0606F280` returning `R0=FFFFFFF8` while the response-control byte at `060662E4` is `00`. BIOS accepts the same path when CR1 supplies a periodic response and the copied control byte has bit `20` set. The repeated commands were therefore normal retries of a non-periodic Busy report, not buffer corruption or another CMOK timeout.

The final sector drain now schedules periodic Busy `2080,4101,0100,00AF` with SCDQ 1,000 master instructions after command `62`, while retaining the already matched immediate `62=0B44` and first HIRQ-poll `CMOK|EHST` edge. PEND is deliberately not asserted here: an A/B run showed no benefit, and it remained latched as an unsupported extra event. Smoke coverage verifies the deferred report and that SCDQ is exposed without PEND.

A clean 128M automatic run issues only one final command `00` at instruction 126,667,265, then returns to active game code without another CD command. It has no bus fault or unimplemented opcode, ends at master/slave PCs `0606B694`/`06005FA2`, and records 32,211,122 Work RAM High writes. The final response remains `2080,4101,0100,00AF`, with HIRQ reflecting the still-unacknowledged event bundle.

This removes the dense Busy polling tail but does not yet reproduce the reference cleanup chain `51(count=0),00,48,00,44,42,46`. Continue by differentially tracing the callback or acknowledgement after BIOS accepts the periodic response. Preserve both the matched final `62 -> 00` HIRQ ordering and the newly proven periodic-control transition.

## July 20 Periodic Pause Cleanup Release

The accepted periodic report must describe Play end rather than remain Busy. Publishing the Mednafen-shaped periodic Pause response `2100,4101,0100,00AF` 1,000 master instructions after final command `62` releases the missing task path without adding an early PEND or SCDQ assertion. The already-latched SCDQ bit is preserved. The low byte is deliberately `00`: the earlier post-Play differential captured this exact form, and the local command trace still has HIRQ `0BC4` before its first post-drain `51`.

SystemRegis now advances from `62,00` through `50,51(count=0),00,48,00,44,42,46`, then issues the exact next reference Play request `1080,12BB,0080,000C`. Its following Busy/FAD-`12BB` polling pattern `51,00,00` also matches Mednafen while the modeled long seek is in flight. The earlier apparent runaway is therefore normal game streaming activity, not another final-drain stall.

One narrow ordering difference remains: SystemRegis asks for buffer size with command `50` before the first zero-count `51`, whereas the reference goes directly from the final `00` to `51`. Continue by comparing the BIOS task selection immediately after the `2100` descriptor is accepted, then validate the long-seek completion toward FAD `12C7`. Preserve the matched `62 -> 00`, periodic Pause, filter cleanup, and `12BB` Play request.

The focused Mednafen CPU trace corrected the interpretation of that ordering difference. After the sixth `62`, the reference never accepts a `2100` descriptor in this window: it consumes ordinary Busy `0080,4101,0100,00AF` and reaches the first zero-count `51` without the modeled final-drain callback. SystemRegis still needs its Pause transition to release that task and therefore runs the extra background `50`; do not hide that command with a BIOS-specific shortcut. The remaining difference belongs to command/event scheduling rather than the buffer-size response itself.

The following `1080,12BB,0080,000C` long Play now crosses its 3.5M-instruction deadline correctly. At instruction `130,251,062`, SystemRegis publishes one available sector with `51=0300,0000,0000,0001`, then matches the reference position/status chain at FAD `12C7`: `52=0380,4101,0100,12C7`, `53=0300,0400,0000,0000`, `61=4380,4101,0100,12C7`, `06=0300,0400,0000,0000`, and `62=0380,4101,0100,12C7`. Multi-sector long seeks now enter Play and expose only the just-completed sector (`12C6`) instead of publishing the entire requested range or remaining in Seek. Single-sector long seeks retain the earlier reference-shaped Seek behavior.

The next differential is the HIRQ bundle around this first long-Play transfer. Mednafen has `0B84` after `52` and `0B44` for `61`, while SystemRegis currently has `0B81` and `0B43`. Consequently, after the first `62` and zero-count `51`, the reference continues to `00,48,00,44,42,46`, but SystemRegis polls empty periodic Play `2380` with `51,00,00`. Compare the CMOK/DRDY/CSCT acknowledgement and delayed ESEL edges here; preserve the now-matched CR words, one-sector availability, data FAD, and `12C7` pickup position.

## July 20 Long-Play Stored-Sector Completion

The long-Play sector now owns CSCT from its publication through `52,53,61,06,62`. Final deletion returns the existing Play descriptor `0380,4101,0100,12C7`, retains CSCT without EHST, and changes the internal drive state so the following status command returns the reference Busy descriptor `0080,4101,0100,12C7`. Because this register model completes commands synchronously, intermediate command traces retain CMOK alongside CSCT even where Mednafen has already acknowledged CMOK; clearing it immediately stalls the BIOS command helper.

A focused Mednafen CPU trace exposed the completion delay after the seventh `62`: the BIOS response loop enters with `R7=7`, increments it to 8, and then observes CMOK while its HIRQ accumulator is `R6=00000B45`. Long-Play deletion therefore publishes CMOK after 16 byte reads, equivalent to the eighth SH-2 word poll. Smoke coverage verifies all seven early polls, the eighth completion, absence of EHST, the retained CSCT, and the post-deletion Busy drive state. Build, smoke, formatting, and diff checks are clean.

The 130.3M NiGHTS acceptance now matches the visible final chain through `62=0380,...,12C7` at HIRQ `0B44` and the following `00=0080,...,12C7` at HIRQ `0B45`. It still repeats command `00` rather than issuing `48`; the remaining architectural difference is inside the BIOS CR-response read loop. In Mednafen the loop reaches `R7=8`, while the current SystemRegis path re-enters with `R7=0` even though `R6=00000B45` and the descriptor agree. Continue from `/tmp/systemregis-post62-v8.trace` and the `SYSTEMREGIS_FINAL_CLEANUP_EXEC` records near line 256842 of `/tmp/mednafen-final-cleanup.log`; compare the path around `0606839A..060683B6` before changing more CD status or HIRQ bits.

The Busy interpretation above mixed two distinct cleanup contexts. The focused CPU trace and `R7=7 -> 8` observation belong to the earlier final drain at FAD `00AF`; the actual Mednafen command log at FAD `12C7` retains Play. A new trace triggered on the eighteenth `62`, the real `12C7` deletion, establishes the reference chain as `62,00,51(count=0),00,48`. Immediate HIRQ is respectively `0B44,0BC4,0BC4,0BC4,0B84`. The BIOS sees asynchronous CMOK on word polls 9, 15, 9, and 9 for the first four commands. SystemRegis now keeps Play after deletion, exposes EHST while retaining CSCT, and models those completion edges. Smoke covers the immediate and delayed HIRQ states plus the zero sector count.

The 130.27M acceptance is fault-free and now matches Play plus immediate `0BC4` for post-delete `00` and `51`, but task ordering is still `62,00,00,51,00,51,00,...` rather than the reference `62,00,51,00,48`. The former hard CMOK wait is gone; BIOS is actively alternating the zero-count buffer task and status task. Continue with a focused architectural comparison after the first accepted zero-count `51`, especially the task/control bytes around `060662A4..060662E4` and the selector-reset task chosen in Mednafen. Do not reintroduce Busy or immediate CMOK: both are now disproven by the FAD-specific reference trace in `/tmp/mednafen-12c7-exec.log` and `/tmp/mednafen-nights-cdb52.log`.

## July 20 First Post-Deletion EHST Edge

The CPU trace corrects the command-log-only interpretation of the first status HIRQ. After the FAD-`12C7` deletion, Mednafen's BIOS accumulator sees `0B44` for word polls 1-6, `0BC4` on poll 7, and `0BC5` on poll 15. SystemRegis previously exposed EHST immediately even though its CMOK timing was correct. The CD block now supports a midpoint HIRQ edge: the first post-deletion status retains CSCT alone, adds EHST after 14 byte reads, and adds CMOK after 30 byte reads. Smoke verifies the immediate state and exact poll 7/15 transitions; build and the 130.27M acceptance remain fault-free.

This correction does not remove the extra status command because the BIOS wait loop branches on CMOK, not EHST. The new trace still produces `62,00,00,51,00,51,...`.

## July 21 Scheduler-Operand Correction

The apparent `06066EDC = 3` versus `12` difference above was caused by comparing different pipeline points: after the load, both SystemRegis and Mednafen have `R0=3`. Decoding the following SH-2 instructions with scaled longword displacements identifies the actual operands as global quantum `0606662C` and task accumulator `06066EAC`. At the first post-delete helper invocation, Mednafen loads `07E0` and `5800`, then stores `5FE0`; SystemRegis loads `0800` and `0000`, then stores `0800`.

A dedicated WRAM write probe in the temporary Mednafen reference tree confirms that `5FE0` is the first write to either watched field after the eighteenth command `62` begins. The missing `5800` therefore predates that command and cannot be caused by the recently added read-count-based EHST/CMOK staging. The next differential is the earlier initialization, accumulation, or reset path for `06066EAC`. Preserve the matched HIRQ waveform and avoid a command-specific scheduler shortcut.

## July 21 Task-Lifecycle Reset Differential

The accumulator reset is now bounded to the interval between the two relevant Delete Sector Data commands. SystemRegis command `62` occurrence six adds `0800` to `06066EAC`, but task destruction at `0606C3F2` clears it at instruction 126,742,645 and the callback cleanup delay slot at `0606CC6E` clears it again at 126,747,233. Both occur before occurrence seven and the FAD-`12C7` completion. The temporary Mednafen probe triggered at occurrence seventeen instead sees `5000 + 0800 -> 5800`, then quantum `07E0` and `5800 + 07E0 -> 5FE0` at the following delete; its matching `0606C3F2` and `0606CC6E` clears occur only afterward.

Decoding caller `0606B418..0606B438` identifies the gate as the lifecycle word at task offset `+0x34`, address `06066ED8` for the active task. Zero branches directly to callback `0606C426`; six takes the same callback after an explicit compare; every other value skips callback and cleanup. The SystemRegis trace enters with zero. CLI task watches now include master-instruction index, command-`62` occurrence, last CD command, and a bounded watch on `06066ED8`. Continue at the write that transitions this lifecycle word and at destructor caller `0606BDD4..0606BDF6`. The evidence rules out scheduler arithmetic and the already-matched post-delete HIRQ edge; the modeled event sequence ends this BIOS task one lifetime too early.

## July 21 Multi-Sector Long-Play Stream

The `1080,12BB,0080,000C` request is a twelve-sector stream, not one sector at the range endpoint. Mednafen exposes FADs `12BC..12C7` individually through repeated `51,52,53,61,06,62` cycles. The CD block now keeps the unexposed sectors pending, publishes one sector per arrival, advances the partition and pickup FAD after each deletion, and does not enter the special final-deletion state until the twelfth sector. The first pickup retains Seek at FAD `12BC`; after eight post-delete status reports the drive enters Play, and later arrivals report ordinary Play without the previous spurious periodic bit. Smoke coverage drains all twelve distinct sectors and verifies the delayed 140,000-instruction arrival boundary between them.

The automatic NiGHTS run improves from the old collapsed FAD-`12C7` transfer to a complete first streamed-sector command path and publication of the next FAD `12BD`. It still does not consume that second sector: BIOS remains in its status helper with the task lifecycle at five and repeatedly reads `0380,4101,0100,12BD`. A reference-shaped experiment that replaced synchronous `CMOK|CSCT` with immediate `EHST|CSCT` was deliberately removed; it stalled the same BIOS job before `52` and also regressed earlier single-sector boot traffic. The next differential must therefore model the asynchronous command-completion/acknowledgement path around `53 -> 61` within the existing synchronous register architecture, rather than changing the sector sequence or forcing a CD interrupt. `HIRQMASK` is zero in both SystemRegis and Mednafen here, so an SCU CD interrupt is not the missing wakeup.

## July 21 Streamed-Sector Lifecycle Completion

The stalled lifecycle-five path was the completion bundle of each intermediate Delete Sector Data command. Its timing was already correct: SystemRegis and Mednafen both expose CMOK on the ninth BIOS word poll. Mednafen additionally leaves EHST asserted after BIOS acknowledges CMOK, producing the observed `0BC4` state that returns the task to polling for the next sector. Intermediate long-Play deletions now publish `CMOK|EHST` at that existing completion edge, while the twelfth/final deletion retains its separately verified CMOK-only completion and final-drain waveform.

The streamed data FAD also has an intentional one-sector offset from the reported pickup. A single-sector seek reports pickup FAD `00A7`, but the transferred PVD begins in sector `00A6` with word `0143`. The partition therefore uses the completed data-sector FAD (`playEndFad - pendingCount`), not `_currentFad`; smoke coverage now pins this distinction as well as the intermediate-versus-final HIRQ bundles.

A clean 131M NiGHTS run advances command `62` from occurrence seven to eleven, proving that BIOS consumes multiple streamed sectors instead of stopping after the first. The 133M acceptance completes the twelve-sector job: lifecycle six records `requested=processed=12` at command-`62` occurrence eighteen, response FAD advances through `12CB`, and the game starts a subsequent four-sector transfer which raises the command counts to nineteen each for `52,53,61,62`. The run is fault-free and ends at valid game PCs `06007066`/`06005FA0`. Continue from this later CD workload; do not revisit the resolved stream publication, pickup/data-FAD distinction, or intermediate EHST completion unless a new reference trace contradicts them.

## July 21 Continuous Long-Play Buffering

The later `1080,0B06,0080,0010` workload exposed a different stream-model error. SystemRegis armed the next-sector timer only after the partition became empty, so a stored sector stopped the drive clock and the BIOS could never collect the multi-sector batches seen in Mednafen. At instruction 159,468,372 the BIOS consequently asked command `52` to calculate three sectors while only one was buffered and received error status `8380`, then repeated command `00` indefinitely.

Long Play now keeps its 140,000-instruction sector-arrival clock running while sectors remain stored. Each arrival appends to the partition, deletion from offset zero advances the partition data FAD by the number removed, and deleting a batch no longer restarts an already active arrival timer. Timer processing is ordered so a newly armed stream deadline does not consume the bulk seek-completion interval that created it. Smoke coverage buffers four sectors without draining the first, transfers and deletes the complete 4,096-word batch, and verifies that the emptied partition refills to four sectors.

The clean 161M acceptance crosses the former failure point: commands `52,53,61,62` advance from occurrence 69 through 73, the BIOS copies four 2,048-byte sectors to `06002000..06003FFF`, the task reaches lifecycle six, and the master returns to game code at `0600722C` without a fault. At 180M the counts reach 75 each, Reset Selector follows, the drive reports periodic Pause `2380,4101,0100,0B26`, and the master remains in valid game code at `0602F244`.

This is not yet a new visible frame. The richest complete VDP1 list remains the eight-sprite Sega copyright list near 89.7M, and the 180M image is byte-identical to the old dump. PTMR-trigger and command-END-write probes were tested and removed: neither found a later complete command chain, despite VDP1-area writes rising from about 49,000 at 161M to 184,000 at 180M. The next blocker is therefore before new VDP1 command generation, not a missed WaylandForge snapshot. Continue by comparing the post-load game/VDP initialization path after the final `48` and Pause transition; preserve continuous CD buffering and do not add a renderer-side frame-retention workaround.

## July 21 SH-2 FRT Input Capture Trigger

The post-load timeout at `0602F242..0602F24A` polls FTCSR bit `0x80` at `FFFFFE11`. The previously passive `01000000..017FFFFF` and `01800000..01FFFFFF` areas are the slave and master SH-2 free-running-timer input-capture triggers. They now pulse the corresponding CPU-local FTCSR flag, with separate trigger devices wired by both the CLI and WaylandForge host. Smoke coverage verifies that word writes through the cache-through aliases trigger only the selected CPU.

The 180M acceptance materially advances after this fix. Master PC moves from the FRT timeout at `0602F244` to the game-system path at `06004BDE`, master internal-register reads drop from about 3.96M to 1.02M, high-WRAM writes increase by about 1.8M, and the two FRT trigger areas record 326 and 330 writes. CD command counts remain healthy at 75 each for `52,53,61,62`, with periodic Pause at FAD `0B26`. The VDP1 image is still the old copyright frame.

The new boundary is a signed-byte poll at `06004BDE..06004BE2`. The setup and callback match the Mednafen reference through `R0=25FE00A0`, `GBR=060FFC00`, `R3=FFFFD7FC`, and `R4=FFFFDFFC`; SystemRegis then reads cached byte `060FFC03` as zero and loops. A trial that retained SCU pending status after CPU acceptance did leave this loop, but caused an interrupt storm and reset back into BIOS, so it was removed. Continue by comparing master-cache state, the writer/purge provenance of `060FFC03`, and the reference CCR state. Preserve the FRT mapping and current one-shot SCU delivery behavior.

## July 21 SCU Direct DMA0

The `06004BDE` loop is the normal master event wait, not a cache deadlock. A bus-level provenance probe showed that VBlank handling writes `FF` to `060FFC03`, the loop consumes it, and the event epilogue clears it at `06004C02`; Mednafen follows the same sequence. The next reference interrupt is SCU vector `4B`, DMA0 end. SystemRegis previously latched the SCU DMA registers without executing them or raising completion.

NiGHTS repeatedly programs direct/manual DMA0 as source `060FFCE0`, destination `25F80000`, byte count `0120`, address-add `0101`, enable/start `0101`, and mode `00000007`. The SCU device now executes that incrementing direct-transfer form through the normal Saturn bus, preserves optional source/destination update semantics, records bounded completion telemetry, raises the level-specific DMA-end status bit, and the CLI and WaylandForge deliver DMA2/1/0 end as vectors `49/4A/4B` at levels `6/6/5`. Indirect mode and other address-add forms remain intentionally unsupported rather than being approximated incorrectly. Smoke coverage verifies a Work RAM to VDP2-register DMA and DMA0-end pending/acknowledgement.

The clean 166M acceptance completes 112 copies of `0120` bytes: VDP2-register writes rise from 35,456 to 67,712, exactly 32,256 additional writes. It remains fault-free and retains healthy CD/FRT progress. The complete VDP1 capture is still the same copyright frame, so direct DMA0 is necessary hardware behavior but not yet the visible-game-frame boundary. Continue by comparing the post-DMA event work and later VDP1 command producer against Mednafen; do not remove the now-observed DMA0-end path or revive the disproven cache-poll hypothesis.

## July 21 VDP1 Draw End

NiGHTS's final SCU mask `FFFFD7FC` leaves VDP1 draw end unmasked alongside VBlank and DMA0, and the game writes VDP1 `PTMR` 134 times by 166M instructions. The former generic register stub could only return the constant `EDSR=2`; it never interpreted `PTMR` or raised SCU bit 13. `Vdp1RegisterBusDevice` now retains that observed EDSR value, completes manual starts immediately, arms automatic starts at VBlank-IN and completes them at VBlank-OUT, records bounded start telemetry, and raises draw-end. The CLI and WaylandForge deliver the pending event as level 2 vector `4D`.

The clean 166M A/B remains fault-free but does not change the visible boundary: final master/slave PCs remain `06004BDE`/`06005FA0`, VDP2 register writes remain 67,712, and the VDP1 image remains byte-identical at SHA-256 `9d4916c718efa8604f44ff744c2c40118ede43968d7dea4d4ac4212c9268e00e`. Draw-end is required SCU/VDP1 behavior, but it is not sufficient to produce the first game frame. Continue with a reference interrupt/event trace after DMA0 and draw completion, or compare the later VDP1 command producer; do not treat the normal `060023F0..060023F6` flag wait as the final blocker.

A filtered two-minute Mednafen SCU trace establishes the steady frame ordering as VBlank-IN, two or four DMA0-end edges, VBlank-OUT, then VDP1 draw-end. The automatic VDP1 model was therefore refined so VBlank-IN arms the draw and VBlank-OUT completes it instead of completing immediately at VBlank-IN. The new 166M telemetry confirms NiGHTS uses only automatic mode at this checkpoint: `PTMR=0002`, 1,447 automatic completions, and zero manual starts. The reordered acceptance remains byte-identical and reaches the same PCs, so the remaining visible blocker lies downstream in the later VDP1/VDP2 producer state rather than missing DMA0/draw-end events or their frame ordering.

## July 22 VDP1 Command-List Length Differential

A correlated local Mednafen probe now identifies the first concrete late-frame divergence. Both emulators use the same SCU DMA0 transfer families for VDP1: large command/texture payloads from `060F0000` and `060FC000`, followed by command lists from `060DC000` to `05C00000` and the companion `060E2000` to `05C06000` transfer. This rules out the VDP1 address map, command-link decoding, and the choice of DMA source/destination as the immediate fault.

The transfer length differs before the copy begins. Mednafen's `060DC000 -> 05C00000` list starts at `0xA2` bytes, grows by `0x40` bytes per frame, and stabilizes at `0x16C2`. SystemRegis's corresponding late transfers remain at `0x82` bytes. Only the clip/local-coordinate header and the start of the first sprite command are therefore refreshed; the remaining VDP1 VRAM contains stale data, producing the observed raw link `0x0C00` and invalid jump to byte address `0x06000`. The visible stale copyright frame is now explained by an incomplete producer list rather than renderer retention.

`ScuRegisterBusDevice` retains a bounded 256-transfer history and the CLI groups its recent source/destination/count tuples. The clean 166M acceptance remains at master/slave PCs `06004BDE`/`06005FA0`, with 488 completed SCU DMAs and 1,447 automatic VDP1 draws. Continue by tracing the game-side count builder and the writes to the `060DC000` command buffer; do not change the already-matched SCU DMA addressing or compensate by extending the transfer in the device.

The extended 190M falsifier confirms this is not merely a short observation window: 43 recent `060DC000` transfers still use `0x82`, and the rendered hash remains `9d4916c718efa8604f44ff744c2c40118ede43968d7dea4d4ac4212c9268e00e`. The master rebuilds the header at `0601F0A0`, writes the first sprite command at `06047BE2..06047C02`, and links it at `0601F1A2/0601F1BC`. Its selected object/descriptor inputs are `R1=060D0070` and `R5=0604DA20`; the corresponding Mednafen producer uses `R1=060D00D0`, `R5=0604544C`, then advances through `060D0130`, `060D0190`, and later entries. The next differential is therefore the active object-list head/selection before the sprite builder, not the builder's stores or SCU DMA execution.

## July 22 Saturn Backup RAM and Scene Branch

The first active-object differential led through the scene selector at `060CB061`. Both SystemRegis and Mednafen write signed byte `FF` from `060040B6/060040BA`, take the negative branch at `0604CB38`, remove the initial `060D0000` node, and call the game backup setup routine at `0600598C`. SystemRegis returns `R0=0` at `0604CC00`, takes the expected empty-save branch, and enters the format/save path at `06005C96`. The former zero-filled generic memory stub was nevertheless inaccurate hardware: Saturn internal backup RAM is 32 KiB on the low byte of a 16-bit bus, with open-bus `FF` on the even byte lane and a repeated `BackUpRam Format` signature in the first 64 logical bytes.

`SaturnBackupRamBusDevice` now models that lane mapping, mirroring, write behavior, and initial signature. `SaturnSystemMap.BackupRam` exposes the device, and CLI option `--dump-final-backup-ram` writes its logical 32 KiB snapshot. Smoke coverage verifies the signature, interleaved byte lane, cache-through write, and physical mirror.

The clean 190M NiGHTS run creates a structurally valid score file. Its 32 KiB backup image differs from a clean Mednafen-created image at only nine bytes: the two copies of the save timestamp/checksum metadata. The remaining 32,759 bytes, including allocation header, `NIGHTS___01`, score payload, and filesystem layout, are identical. Backup RAM and the empty-save branch are therefore ruled out as the cause of the missing VDP1 producer. At 190M SystemRegis still has 1,507 automatic VDP1 completions, `PTMR=0000`, and no late `060DC000` command-list DMA; continue after the completed `06005C96` save path and compare the reference return/control state before the next object manager callback.
