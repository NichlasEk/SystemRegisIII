# SystemRegisIII Bringup Plan

## North Star

Build a permissively licensed, modular Sega Saturn core first. Keep the core headless, deterministic, and testable before any UI or Android shell work. The host layer should stay replaceable so the same core can later plug into a custom desktop shell, EutherDrive Android, or test harnesses.

## Current Baseline

- BIOS zip files live under `bios/` and are ignored by git.
- The core has a page-mapped Saturn bus, BIOS ROM mapping, Work RAM Low/High, named stubs, and optional tracing.
- The CLI can run BIOS in single-master mode or experimental `--dual-sh2` mode.
- The BIOS currently clears both Work RAM regions and reaches a stable delay loop at `0x00001D3E` with no unimplemented opcodes in the verified 5M-instruction run.

## Phase 1: Make Dual SH-2 Real Enough

1. Add CPU-local SH-2 internal register handling.
   - Master and slave must not read the same CPU-id/status values.
   - Keep shared external bus/RAM behavior intact.
   - Preserve diagnostics for internal register reads/writes.

2. Keep `--dual-sh2` experimental.
   - Default CLI run remains the stable single-master baseline.
   - Dual mode reports master/slave PC, SR, unimplemented instructions, and touched hardware.

3. Improve frame stepping.
   - Interleave master/slave execution in small slices.
   - Keep non-CPU devices stepped once per frame until they have their own clocks.

## Phase 2: Hardware Stubs That Behave

Implement narrow, BIOS-driven device behavior only when traces prove it is needed.

1. SMPC
   - Command/status latches.
   - Minimal reset/input/system-manager status.

2. SCU
   - Interrupt/status/timer surfaces.
   - DMA/status reads that BIOS probes.

3. VDP2/VDP1
   - Status register reads and benign writes.
   - No full rendering until BIOS/CD flow needs it.

4. CD Block
   - Command/status shell first.
   - Sector source later via host-side image reader.

## Phase 3: SH-2 Coverage

Add instructions only when the BIOS or focused tests hit them.

- Implement the missing opcode and nearby addressing forms.
- Add smoke/regression coverage for semantics-sensitive flags, delay slots, and memory modes.
- Avoid large decoder rewrites until coverage demands it.

## Phase 4: Bringup Probes

Keep CLI diagnostics compact and deterministic.

- PC/SR for master and slave.
- RAM write ranges and counts.
- Touched hardware stubs.
- First/last unimplemented opcode per CPU.
- Recent trace ring, never unbounded trace output.
- Later: loop detector for repeated PC pairs and hardware wait loops.

## Phase 5: ROM/CD Path

Only after BIOS init is deterministic enough:

1. Add host-side disc image abstraction.
2. Support a simple ISO/BIN/CUE path.
3. Feed sectors through `CdBlock`.
4. Prefer a tiny homebrew/test image before commercial games.
5. Try Night Into Dreams after CD, VDP, SCSP, and input surfaces are meaningfully wired.

## Phase 6: Shell Later

- Skip Avalonia.
- Do not build UI until the core exposes stable video/audio/input contracts.
- Keep the shell separate from the MIT core.
- Target a cooler custom host and later EutherDrive Android integration.

## Working Rule

Work in small pushable slices:

1. Make one architectural improvement.
2. Verify with build, smoke, and BIOS CLI.
3. Commit and push.
4. Record the next blocker in this plan or CLI output.

## Progress Log

- `648976b`: Added this plan and CPU-local SH-2 internal register buses. Master reads CPU-id `0`, slave reads `0x20000000`, so BIOS can take distinct master/slave paths while external RAM remains shared.
- `d923e2e`: Added hot-PC reporting to the CLI. Current BIOS evidence shows master hot at `0x00001D3C/0x00001D3E` and slave hot at `0x00000240/0x00000242` in dual mode.
- `2246675`: Added bus fault summary output. `--dual-sh2 --simulate-slave-ready` currently reports `Slave SH-2 fault at 0x06100000` after running through empty high RAM.
- `57c0d4f`: Fixed SH-2 indexed move decoding so `0x0000` is not treated as a valid instruction. The forced slave-ready path now correctly reports unimplemented `0x0000` at `0x06000600`.
- `3924477`: Added BIOS-driven SH-2 coverage for displacement moves, PC-relative word loads, predecrement stores, control-register moves, compare/subtract forms, and shift/rotate forms. Added a conservative `0x05800000..0x058FFFFF` B-bus mirror stub so SH-2 cache-through reads like `0x25890018` no longer fault.
- `908a51d`: Identified the `0x25890018..0x25890024` BIOS probe as the CD Block ID string `\0CDBLOCK` and modeled those four read-only words in the register mirror. Added a conservative `0x04000000..0x04FFFFFF` A-bus probe stub for the cache-through `0x24FFFFFF` BIOS read.
- `7fe0fac`: Made the CD Block register mirror switch from ID signature to a minimal status snapshot after the BIOS command write to `0x25890008`, and added SH-2 `BT/S`, `RTE`, `CMP/GE`, and `LDC.L @Rn+` control-register coverage.
- `70d37db`: Added a minimal CD Block HIRQ `CMOK` latch at `0x25890008` after the BIOS status transition and filled SH-2 `NOT Rm,Rn`. This moves BIOS through the first HIRQ wait and into repeated CR/HIRQ polling with no unimplemented opcodes.
- `753d185`: Split the CD Block register mirror out of `SaturnSystemMap` into a dedicated `CdBlockRegisterBusDevice`, added HIRQ clear/mask-ish behavior, CR command latches, and CLI reporting for the last CR command. Yabause was used only as a behavioral/register reference for HIRQ/CR naming and status shape; no GPL implementation code was copied.
- Current slice: Implemented CD Block command `0x01` (`Get Hardware Info`) as a clean room register response, added SCSP register write-back for BIOS audio init, and filled the SH-2 coverage that BIOS hit after leaving the CD loop: `CMP/HI`, `SHLR`, and GBR byte immediate `TST.B/AND.B/XOR.B/OR.B`.
- Current slice: Added a dedicated SMPC register device from the official SMPC command model, including command history, immediate status-flag completion, and `SSHON`/`SSHOFF` slave enable state. The CLI now gates experimental slave stepping on SMPC state and prints a compact Work RAM loop probe when BIOS reaches the hot `0x06028314..0x06028318` loop. Official Saturn manuals from antime's Sega documentation archive were used as behavior references for SMPC command numbers, SCSP register map/timer notes, and SCU interrupt/mask direction.
- Current slice: Added repo-local `docs/reference-map.md` with link-only Saturn reference notes and license cautions, added SH-2 interrupt entry with level masking and vector lookup, replaced the generic SCU stub with a narrow interrupt mask/status device, and drove deterministic V-Blank-IN interrupt requests from the CLI. Filled the BIOS interrupt-handler SH-2 opcodes `SHLR16` and `STC.L SR/GBR/VBR,@-Rn`.

## Current Next Blocker

In real dual mode, the verified `40M`-instruction run has no unimplemented opcodes and no bus faults. Master leaves the BIOS CD status polling path, initializes SCSP/VDP-facing registers, runs unpacked code from Work RAM High, services SCU V-Blank-IN interrupts, and still returns to the Work RAM wait around `0x06028314..0x06028318`.

The loop is now identified as a Work RAM change wait:

- `GBR=0x06020000`
- `MOV.L @(0x90,GBR),R0` reads `0x06020240`
- `R4` is initialized from the same address and remains `0x00000000`
- BIOS loops while `[0x06020240] == R4`

SMPC command history before the loop is `0x1A, 0x10, 0x10, 0x19, 0x07, 0x06`, ending in `SNDON`. There is no `SSHON` yet, so the previous slave-ready theory is weaker: BIOS appears to be waiting for an interrupt/tick or sound-init completion flag before enabling the slave SH-2.

With V-Blank-IN enabled, SCU state is `mask=0xFFFFFE7C` and `status=0x00000001`. The BIOS interrupt handler repeatedly writes the CD Block command registers with command `0x00`, then returns to the same Work RAM flag wait. This makes CD command `0x00`/current-status or periodic-status behavior the next likely blocker.

The forced `--simulate-slave-ready` path is a separate blocker: it still runs into empty high RAM and reports a slave bus fault at `0x06100000`, with first unimplemented `0x0000` at `0x06000600`.

The next slice should implement the smallest correct CD status path:

- identify CD command `0x00` response shape from a clean reference,
- add CLI reporting of current CD response CR values, not only the last command CR values,
- model current/periodic CD status enough for the BIOS interrupt handler,
- verify whether `0x06020240` changes and whether BIOS reaches `SSHON`.
