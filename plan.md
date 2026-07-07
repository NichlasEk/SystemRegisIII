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
