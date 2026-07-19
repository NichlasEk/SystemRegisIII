# SystemRegisIII Saturn bringup handoff

Date: 2026-07-19
Branch: `main`  
Implementation checkpoint: `ff10403` (`Advance Saturn post-read file bringup`)

## Current outcome

The automatic NiGHTS boot now passes the old apparent post-Read-File stall without the diagnostic initial-program-load shortcut. The CD command prefix matches the local Mednafen reference through:

```text
... 03,03,10,51,63,06,70,72,74,00,00,73,06
```

The latest 125M acceptance run reaches:

```text
i=93,800,831   74 -> 0080,4101,0100,00AF, HIRQ=0x0DD4
i=109,102,391  00 -> 0180,4101,0100,017D
i=109,102,647  00 -> 0180,4101,0100,017D
i=109,102,913  73 -> 4100,0006,0000,0000
i=109,103,233  06 -> 0100,0006,0000,0000
```

The loop at `0x060111A8..0x060111B0` was not a deadlock. It compares the VBlank-maintained word at `0x060348EC` with the target `0x0082`; SystemRegis enters at `0x0001`, reaches `0x0083` at instruction 106,750,089, and exits normally. Mednafen enters the same wait at `0x0029`.

Read File now distinguishes a complete file from a full 200-sector partition. NiGHTS fills the partition, publishes FAD `0x017D`, and raises `BFUL` without the premature `EFLS` bit. The post-read HIRQ state consequently matches Mednafen (`0x0DDC` before the next command-completion bit).

## Visual status

There is no new visible screen yet. The latest frame dump still contains the same Sega/copyright frame:

- 11 VDP1 commands
- 8 drawable sprites
- 1,623 rendered pixels
- richest capture remains at instruction 89,700,000

The next visual milestone should follow the full 200-sector executable transfer. The remaining expected reference continuation is:

```text
51,63,06
```

## Current blocker

The CD-visible state now matches through the file-info End Data Transfer. The six transferred file-info words also match Mednafen exactly:

```text
0000,00B5,0006,D59C,0000,0000
```

At 115M the master is still at BIOS `0x000042FA`; by 125M it has advanced to `0x00001FD0`, so this is not the old stable wait. It has not yet issued the reference `51`. Continue longer first; if `51` still does not arrive, compare CPU/stack state after the file-info `06` rather than changing the now-matched CR, HIRQ, or payload.

## Important fixes in the latest slices

- Play End is asynchronous and publishes periodic Pause plus `PEND`.
- Play exposes its 16-sector range through partition 0.
- Command `51` returns the buffered sector count.
- Command `63` starts a real FIFO and returns the reference position response.
- The four-byte CD data-port window consumes two words per 32-bit read.
- End Data Transfer reports the full transferred word count without a false periodic bit.
- Post-transfer status releases BIOS into root filesystem loading.
- Root Change Directory, Filesystem Scope, and Read File use reference-shaped two-phase responses.
- Read File buffers at most 200 sectors and completes asynchronously.
- Read File completion reports `0180` rather than the previously inferred `2180`.
- A full 200-sector partition raises `BFUL`; only an actually completed short file raises `EFLS`.
- Current/periodic status preserves the live FAD instead of reverting to track start.
- File-info command `73` reports reference-shaped `4100` DTREQ status.
- Host-transfer counts are 32-bit and a 200-sector FIFO retains all 204,800 words.
- The CLI has a focused post-Read-File trace and a NiGHTS frame-wait word watch.

## Verification

Focused validation:

```bash
dotnet run --project tests/SystemRegisIII.Smoke/SystemRegisIII.Smoke.csproj
dotnet format SystemRegisIII.slnx --verify-no-changes
```

Long automatic acceptance run:

```bash
dotnet run -c Release --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run \
  --bios "bios/Sega Saturn BIOS (J) (1.01).zip" \
  --disc "/home/nichlas/roms/Saturn/NightsIntoDreams/NiGHTS into Dreams... (Japan).cue" \
  --dual-sh2 \
  --simulate-scsp-command-ack \
  --vblank-interval 100000 \
  --instructions 125000000 \
  --summary-only
```

The detailed historical log remains in `docs/saturn-bringup-handoff-2026-07-09.md`.

## Workspace state

At handoff creation, `main` was synchronized with `origin/main` and had no unrelated changes. The local Mednafen reference lab remains under ignored `local/mednafen-lab` and must not be committed.
