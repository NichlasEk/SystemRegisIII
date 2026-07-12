# SystemRegisIII Saturn bringup handoff

Date: 2026-07-12  
Branch: `main`  
Checkpoint: `0a5a284` (`Advance Saturn boot into file loading`)

## Current outcome

The automatic NiGHTS boot now reaches the real `0NIGHTS` file-loading path without the diagnostic initial-program-load shortcut. The CD command prefix matches the local Mednafen reference through:

```text
... 03,03,10,51,63,06,70,72,74
```

The latest 96M acceptance run reaches:

```text
i=93,798,237  70 -> 0180,4101,0100,00A6
i=93,798,522  72 -> 0300,00B0,0100,0002
i=93,800,831  74 -> 0080,4101,0100,00AF, HIRQ=0x0DD4
```

Read File then models a 200-sector asynchronous fill, publishes periodic Pause at FAD `0x017D`, and raises deferred EFLS. This matches the observed Mednafen ordering: command `74` does not raise EFLS immediately.

## Visual status

There is no new visible screen yet. The latest frame dump still contains the same Sega/copyright frame:

- 11 VDP1 commands
- 8 drawable sprites
- 1,623 rendered pixels
- richest capture remains at instruction 89,700,000

The next visual milestone should follow the full 200-sector executable transfer. The expected reference continuation is:

```text
00,00,73,06,51,63
```

## Current blocker

After deferred Read File EFLS, SystemRegis remains in the BIOS response-table scan and does not yet issue the first reference status command `00`. The device-visible state is already:

```text
CR=2180,4101,0100,017D
HIRQ includes deferred EFLS
```

Do not retune the already matched `70`, `72`, or `74` immediate responses without new reference evidence. The next focused differential should inspect the BIOS response descriptor/control byte after deferred Read File EFLS and compare it with Mednafen, as was done successfully for the earlier post-session `03,03 -> 10` boundary.

## Important fixes in the latest slices

- Play End is asynchronous and publishes periodic Pause plus `PEND`.
- Play exposes its 16-sector range through partition 0.
- Command `51` returns the buffered sector count.
- Command `63` starts a real FIFO and returns the reference position response.
- The four-byte CD data-port window consumes two words per 32-bit read.
- End Data Transfer reports the full transferred word count without a false periodic bit.
- Post-transfer status releases BIOS into root filesystem loading.
- Root Change Directory, Filesystem Scope, and Read File use reference-shaped two-phase responses.
- Read File buffers at most 200 sectors and defers EFLS until fill completion.

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
  --instructions 96000000 \
  --summary-only \
  --dump-vdp1-frame /tmp/nights-96m-efls.ppm
```

The detailed historical log remains in `docs/saturn-bringup-handoff-2026-07-09.md`.

## Workspace state

At handoff creation, `main` was synchronized with `origin/main` and had no unrelated changes. The local Mednafen reference lab remains under ignored `local/mednafen-lab` and must not be committed.
