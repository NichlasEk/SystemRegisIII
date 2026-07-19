# SystemRegisIII Saturn bringup handoff

Date: 2026-07-19
Branch: `main`  
Implementation checkpoint: `6e9025b` (`Model Saturn CD software reset completion`)

## Current outcome

The automatic NiGHTS boot now passes both the post-file-info status boundary and the subsequent CD-block software-reset wait without the diagnostic initial-program-load shortcut. The CD command prefix matches the local Mednafen reference through the full buffered executable transfer:

```text
... 70,72,74,00,00,73,06,51,63,06,51,63,06,75,04
```

The latest 125M acceptance run reaches:

```text
i=93,800,831   74 -> 0080,4101,0100,00AF, HIRQ=0x0DD4
i=109,102,391  00 -> 0180,4101,0100,017D
i=109,102,647  00 -> 0180,4101,0100,017D
i=109,102,913  73 -> 4100,0006,0000,0000
i=109,103,233  06 -> 0100,0006,0000,0000
i=109,141,511  51 -> 0100,0000,0000,00C8
i=109,141,790  63 -> 4180,4101,0100,017D
i=109,654,434  06 -> 0103,2000,0000,0000
i=109,656,763  51 -> 0100,0000,0000,00C8
i=109,657,042  63 -> 4180,4101,0100,017D
i=109,705,285  06 -> 0100,4ACE,0000,0000
i=109,705,562  75 -> 0000,4101,0100,017D
i=109,770,684  04 -> 0000,4101,0100,00A6
```

Command `04 0401` is a software reset. After its delayed combined completion HIRQ (`0x0BC1`), the focused 110M run leaves the BIOS wait and executes the loaded program in Work RAM High, ending at `PC=0x060023F6`. This is the first automatic handoff into the loaded executable on the normal path.

The loop at `0x060111A8..0x060111B0` was not a deadlock. It compares the VBlank-maintained word at `0x060348EC` with the target `0x0082`; SystemRegis enters at `0x0001`, reaches `0x0083` at instruction 106,750,089, and exits normally. Mednafen enters the same wait at `0x0029`.

Read File now distinguishes a complete file from a full 200-sector partition. NiGHTS fills the partition, publishes FAD `0x017D`, and raises `BFUL` without the premature `EFLS` bit. The post-read HIRQ state consequently matches Mednafen (`0x0DDC` before the next command-completion bit).

## Visual status

There is no new visible screen yet. The latest frame dump still contains the same Sega/copyright frame:

- 11 VDP1 commands
- 8 drawable sprites
- 1,623 rendered pixels
- richest capture remains at instruction 89,700,000

The full 200-sector executable transfer now completes, the reset wait releases, and control reaches the loaded executable. The richest visible capture has not changed yet. By 125M the run eventually enters the BIOS stop loop at `0x00000530`, so the next visual milestone is beyond the new program-entry boundary.

## Current blocker

The previous blockers were the missing periodic idle report after the file-info End Data Transfer and the missing asynchronous completion for `04 0401`. The six transferred file-info words already matched Mednafen exactly:

```text
0000,00B5,0006,D59C,0000,0000
```

The BIOS compares the response successfully, then rejects its control byte until bit `0x20` appears. Mednafen changes the byte from `0x01` to periodic Pause `0x21` after 283 polls. SystemRegis previously left it at `0x01` forever. A delayed periodic status at 38,000 master instructions now reproduces that transition and releases the expected `51,63,06` sequence.

Mednafen then completes the `04 0401` software reset asynchronously and raises `MPED|EFLS|ECPY|EHST|ESEL|CMOK` (`0x0BC1`). SystemRegis previously waited for eight explicit status polls that never occur on this path. The new delayed completion releases the BIOS into the loaded program. The current blocker is later: at 110M the program is active around `0x060023F0`, while the 125M run eventually reaches the BIOS stop loop at `0x00000530` after additional SMPC activity. Isolate the first divergence between those points; do not return to CD payload guessing unless a trace leads back there.

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
- Post-file-info idle time now publishes periodic Pause (`0x21`) after the observed delay.
- CD-block software reset completes asynchronously with the reference `0x0BC1` HIRQ bundle and Pause transition.
- The normal boot path now hands control to the loaded Work RAM High executable.
- The CLI can capture a post-file-info SH-2 trace, WRAM snapshot, focused return-slot activity, and an arbitrary instruction window.

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
