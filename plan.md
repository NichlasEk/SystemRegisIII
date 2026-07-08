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
- Current slice: Modeled CD Block command `0x00` as an explicit current-status response, exposed response CR values in the CLI, and changed the no-media bringup default from `<PAUSE>` to `<NODISC>`. This keeps the headless BIOS run honest until a host disc image is mounted.
- Current slice: Added focused Work RAM watches around `0x06020230..0x0602024F` and `0x06020720..0x0602075F`, plus compact BIOS code/data windows for the wait loop, SCU V-Blank-IN handler, callback table, and V-Blank helper code. The watched wait flag at `0x06020240` is only written as zero so far; nearby `0x0602024C` points at callback/state storage `0x06020728`, while the V-Blank callback table at `0x06000A00` points to `0x06028D64` and `0x06028D9E`. PC heat now proves V-Blank callback/helper code is reached, including `0x06028934`.
- Current slice: Added SCU V-Blank-OUT pending/status support and deterministic CLI raising between V-Blank-IN ticks. The 40M BIOS run still stops at `0x06028318`; the wait flag and callback-state table remain unchanged, so V-Blank-OUT alone is not the missing activation source.
- Current slice: Used Mednafen's local Saturn source only as a GPL black-box/behavioral oracle and corrected the active SCU interrupt-mask interpretation: bits `7/8` are SMPC/PAD, not timers. Built a Saturn-only Mednafen oracle binary in `/tmp/systemregis_mednafen_probe/mednafen/src/mednafen`, and added a narrow SMPC `INTBACK` completion interrupt path through SCU bit `7`, vector `0x47`, level `8`.
- Current slice result: the 40M BIOS run still stops at `0x06028318` with SMPC pending interrupts drained and `smpc-pending=False`. The next suspect is SMPC `INTBACK` output/status data or PAD interrupt behavior, not basic SCU SMPC interrupt delivery.
- Current slice: Added byte-mapped SMPC IREG/OREG/SR bringup behavior and minimal INTBACK result buffers for system status plus no-peripheral port status. The BIOS now observes `IREG=01,02,F0`, `SR=0x40`, `OREG0=0x40`, area `0x01`, and system status `0x34`, but still stops at `0x06028318`; this weakens the INTBACK-output theory and points the next probe toward CD periodic status or SCSP/sound-init completion.
- Current slice: Added PC-attributed RAM watch writes for the flag and callback-state windows. The latest meaningful flag writes are `0x0602024C = 0x06020728` from `0x060281F0` and `0x06020248 = 0x22` from `0x06028200`; callback-state zero/FE initialization is from `0x06029EDA`. No later writer changes `0x06020240`, so the next slice should disassemble/probe those writer routines and their callers rather than add more blind device status.
- Current slice: Added BIOS code windows for the writer routines. `0x060281F0` is `MOV.L R0,@(0x93,GBR)` and writes `0x0602024C`; `0x06028200` is `MOV.L R0,@(0x92,GBR)` and writes `0x06020248`. The wait loop reads `MOV.L @(0x90,GBR),R0`, so the missing transition is specifically a later `GBR+0x90` write or callback-state activation, not these setup stores.
- Current slice: Extended RAM watches to include read attribution and register context. In the 40M dual-SH2 run, `0x06020240` is read `21,333` times from `0x06028314` with `PR=0x06028310`, `GBR=0x06020000`, and `R0=0`; callback-state window `0x06020720..0x0602075F` has `reads=0`. The BIOS is therefore stuck before it ever scans the callback-state entries; the next probe should focus on the call path ending at `0x06028310/0x06028314` and the hardware condition expected to write `GBR+0x90`.
- Current slice: Added a focused SH-2 instruction decoder to the CLI code windows plus SCU interrupt delivery counters. The BIOS setup registers vector `0x40` to callback `0x06028D64` and vector `0x41` to callback `0x06028D9E`; the latter increments `GBR+0x90` at `0x06028DB0`. The first counter run proved `VBlank-IN` remained pending and starved `VBlank-OUT` completely (`0x41` attempts `0`). Treating deterministic CLI V-Blank ticks as accepted pulses fixes the starvation: `0x41` is now accepted, `0x06020240` increments, and the 40M run advances from the old wait loop to `0x0602BD48`.
- Current slice: Added PC-attributed SMPC register watches and a focused code window for the `0x0602BD48` blocker. BIOS was copying INTBACK OREG bytes in `0x0602BD20..0x0602BD6E`, but the real issue was repeated acceptance of the same generated SMPC interrupt. Acknowledging the SMPC pulse after vector `0x47` is accepted reduces SMPC delivery to `attempts=1 accepted=1`, clears SCU status, and advances the 40M run to BIOS ROM PC `0x00004C58`.
- Current slice: Added the first host-side disc abstraction (`IDiscImage`, `RawDiscImage`) and CLI `--disc <path>`. Mounted media now changes CD current-status from no-disc `CR1=0x0700` to media-present `CR1=0x0200`, and CLI output reports mounted disc name/sector count. A dummy 256-sector raw image confirms the status path but still stops at BIOS ROM `0x00004C58`, so the next blocker is richer CD Block behavior rather than simple media presence.
- Current slice: Corrected the mounted-media current-status response shape using Mednafen only as a GPL behavioral oracle: mounted dummy media now reports `CR1=0x0280`, `CR2=0x4101`, `CR3=0x0100`, `CR4=0x0096` (`STANDBY`, CD-ROM/data bit, track 1, index 1, FAD 150). The 40M dummy-disc run still stops at `0x00004C58` and the last CD command remains `0x00`, so the next blocker is likely CD drive phase/status semantics before BIOS asks for TOC or sector data.
- Current slice: Added a reproducible mounted-CD status probe via `--cd-status busy|pause|standby|play|wait`. In 40M dummy-disc runs, `busy`, `pause`, `standby`, and `play` all still stop at `0x00004C58` with last command `0x00`; `wait` stops earlier at `0x00003C24`. The blocker is therefore not solved by a single mounted status code. The next productive move is to decode/probe the BIOS copy/parse routine around `0x00004C50..0x00004C6A` and the buffer at `0x0601FF64/0x0601FF7C`.
- Current slice: Modeled mounted-media current status as periodic (`CR1=0x2280`) and latched the BIOS-observed mounted status-ready HIRQ mask `0x4658` for command `0x00`. Focused probes around `0x000041E4..0x000042BE` showed BIOS waiting first on accumulated HIRQ masks `0x4618` and then `0x0040`; satisfying those moves the 40M dummy-disc run from `0x00004C58/0x00004C04` to BIOS ROM `0x000032EE`. The next CD blocker is command `0x75` with HIRQ reads returning `0x0041`.

## Current Next Blocker

In real dual mode, the verified `40M`-instruction run has no unimplemented opcodes and no bus faults. Master leaves the early BIOS CD status polling path, initializes SCSP/VDP-facing registers, runs unpacked code from Work RAM High, services SCU V-Blank-IN, V-Blank-OUT, and SMPC interrupts as generated pulses, then returns to BIOS ROM at `0x00004C58`.

The old loop was a Work RAM change wait:

- `GBR=0x06020000`
- `MOV.L @(0x90,GBR),R0` reads `0x06020240`
- `R4` is initialized from the same address
- BIOS loops while `[0x06020240] == R4`

The wait is broken by the V-Blank-OUT callback:

- `0x06028C78` registers vector `0x40` callback `0x06028D64`
- `0x06028C82` registers vector `0x41` callback `0x06028D9E`
- `0x06028DAC` reads `GBR+0x90`
- `0x06028DAE` increments it
- `0x06028DB0` writes it back

The CLI was previously modeling generated V-Blank sources as permanently pending level bits. That let `VBlank-IN` win the priority check forever and starved `VBlank-OUT`. Generated V-Blank ticks are now acknowledged after the SH-2 accepts the interrupt, so the pending pulse does not remain latched indefinitely.

The 40M result after the V-Blank pulse fix:

- Master PC advances to `0x0602BD48`
- `GBR+0x90` / `0x06020240` increments through the V-Blank-OUT callback; latest observed value is `0x0000000B`
- SCU delivery counters: VBlank-IN `attempts=137 accepted=11`, VBlank-OUT `attempts=171 accepted=11`, SMPC `attempts=1,887,413 accepted=75,493`
- final SCU state: `mask=0xFFFFFFFC status=0x00000080`, so SMPC bit `7` remained the visible pending/status source at the next blocker
- SMPC command history reaches another `INTBACK` (`0x10`) after the old loop: `0x1A, 0x10, 0x10, 0x19, 0x07, 0x06, 0x10`
- slave SH-2 is still not enabled by SMPC

That SMPC blocker is now resolved for this bringup model. The focused probe showed BIOS copying INTBACK OREG bytes at `0x0602BD20..0x0602BD6E`; acknowledging the generated SMPC interrupt after SH-2 accepts vector `0x47` prevents repeated handler re-entry. The latest 40M result:

- Master PC advances to `0x000032EE` with mounted dummy media after the CD HIRQ/status-ready update.
- SCU delivery counters: VBlank-IN `attempts=89 accepted=11`, VBlank-OUT `attempts=87 accepted=11`, SMPC `attempts=1 accepted=1`
- final SCU state: `mask=0xFFFFFE7C status=0x00000000`
- CD Block current-status response is still no-media without `--disc`: `CR1=0x0700`, `CR2=CR3=CR4=0`
- CD Block CR reads are now the hot activity again: `2,573,816` reads in the 40M run
- With `--disc /tmp/systemregis_dummy.iso`, CD Block current-status now reports periodic mounted media as `CR1=0x2280`, `CR2=0x4101`, `CR3=0x0100`, `CR4=0x0096`, and `256` mounted sectors.
- The BIOS CD helper around `0x000041E4..0x000042BE` now passes the mounted status-ready waits. The final HIRQ hot read at the new blocker is `0x0041` from `0x000040DA`, and the last command latch is `CR1=0x7500`.

The forced `--simulate-slave-ready` path is a separate blocker: it still runs into empty high RAM and reports a slave bus fault at `0x06100000`, with first unimplemented `0x0000` at `0x06000600`.

The next slice should identify the new BIOS ROM `0x000032EE` blocker and the CD Block behavior expected by command `0x75`:

- keep the focused BIOS ROM code/data window around `0x000032E0..0x00003310` plus the CD helper around `0x000040D0..0x000040F8`
- keep PC-attributed CD Block HIRQ/CR reads around `0x000040DA` and command writes around `0x000040F4`
- decode command `0x75` from clean-room docs or behavioral probes before inventing response data
- keep CD/SCSP/VDP behavior changes evidence-driven from those watches.
