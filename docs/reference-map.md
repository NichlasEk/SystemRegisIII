# Reference Map

This project keeps the emulator core MIT-friendly. Do not vendor or copy text from manuals unless the source license explicitly permits redistribution.

## Vendored Documents

No external hardware documents are vendored yet.

Reason: the most useful Sega Saturn PDFs found so far are marked "SEGA Confidential" and do not provide an open redistribution license. Use them as clean-room behavior references only; do not copy tables, prose, or implementation code into this repository.

## Online References Used

### Antime Sega Documentation Index

- URL: https://antime.kapsi.fi/sega/docs.html
- License status: redistribution license not identified.
- Use: index for official Saturn manuals.
- Handling: link-only reference; do not vendor PDFs.

### SCU User's Manual

- URL: https://antime.kapsi.fi/sega/files/ST-097-R5-072694.pdf
- License status: marked SEGA Confidential; do not vendor.
- Useful clean-room facts:
  - SCU interrupt mask register: `25FE00A0`.
  - SCU interrupt status register: `25FE00A4`.
  - V-Blank-IN interrupt: status/mask bit `0`, vector `0x40`, level `0xF`.
  - V-Blank-OUT interrupt: status/mask bit `1`, vector `0x41`, level `0xE`.
  - SMPC interrupt: status/mask bit `7`, vector `0x47`, level `0x8`.
  - PAD interrupt: status/mask bit `8`, vector `0x48`, level `0x8`.
  - In this repo's current SCU mirror mapping, cache-through `0x25FE00A0/0x25FE00A4` appears at device offsets `0x0E00A0` and `0x0E00A4`.

### Mednafen Sega Saturn Core

- Local source archive inspected outside this repository: `/home/nichlas/mednafen.github.io/releases/files/mednafen-1.32.1.tar.xz`.
- License status: GPL; use only as a black-box or behavioral oracle, never as implementation source for the MIT core.
- Local probe build: `/tmp/systemregis_mednafen_probe/mednafen/src/mednafen`, configured with only the Saturn core, SDL audio/video disabled, and debugger enabled.
- Useful clean-room facts:
  - SCU exposes separate interrupt factors for V-Blank-IN, V-Blank-OUT, SMPC, PAD, DMA, DSP, and external A-Bus sources.
  - SMPC `INTBACK` behavior can raise the SCU SMPC interrupt source.
  - Mednafen has debugger-visible SCU register groups for interrupt level, vector, pending bits, asserted bits, and mask.

### SMPC User's Manual

- URL: https://antime.kapsi.fi/sega/files/ST-169-R1-072694.pdf
- License status: marked SEGA Confidential; do not vendor.
- Useful clean-room facts:
  - SMPC status flag indicates command busy/completed.
  - Input registers are byte-spaced at odd offsets from `0x01`; output registers are byte-spaced at odd offsets from `0x21`.
  - Status register `SR` is at offset `0x61`; status flag register `SF` is at offset `0x63`.
  - INTBACK can return SMPC system status, peripheral data, or both depending on IREG command parameters.
  - `SSHON` command code is `0x02`.
  - `SSHOFF` command code is `0x03`.
  - `SNDON` command code is `0x06`.
  - `SNDOFF` command code is `0x07`.
  - BIOS command history before the current Work RAM wait is `0x1A, 0x10, 0x10, 0x19, 0x07, 0x06`.

### SCSP User's Manual

- URL: https://antime.kapsi.fi/sega/files/ST-077-R2-052594.pdf
- License status: marked SEGA Confidential; do not vendor.
- Useful clean-room facts:
  - SCSP slot registers occupy `0x100000..0x1003F7`.
  - SCSP common control registers occupy `0x100400..0x10042F`.
  - DSP control area begins at `0x100700`.
  - The BIOS hot reads around SCSP offsets `0x700`, `0x710`, and `0x720` are therefore DSP-control-facing reads, not timer register reads.

### System Library User's Guide / CD Communication Interface

- URL: https://antime.kapsi.fi/sega/files/ST-162-062094.pdf
- License status: marked SEGA Confidential; do not vendor.
- Useful clean-room facts:
  - CD status information is an 8-byte response spread across CR1-CR4.
  - `CDC_GetCurStat` issues a CD block command and returns current status/report.
  - `CDC_GetPeriStat` reads periodic response without issuing a CD block command.
  - `CDC_GetToc`/CD command `0x02` returns a data-transfer-ready response and exposes TOC data through the host transfer path.
  - BIOS VBlank interrupt activity currently writes CD Block command `0x00`, which this repo models as current-status response.
  - Mounted dummy media currently reports a simple periodic data-track status response: `CR1=0x2280`, `CR2=0x4101`, `CR3=0x0100`, `CR4=0x0096`.
  - BIOS-observed command `0x75` is modeled as Abort File for bringup, returning mounted periodic status and raising `EFLS` (`0x0200`) with `CMOK`.
  - Current clean-room CD coverage includes register-level `Get TOC`, `Get Session Info`, and `End Data Transfer`, plus a minimal TOC host data FIFO at `0x25890000`.
  - Current clean-room sector coverage includes `Set Filter Range` and `Get Sector Data` for raw disc images, with FAD `150` mapped to raw LBA `0`.

## Current BIOS Bringup Evidence

Verified with:

```text
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --instructions 40000000 --dual-sh2
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --disc /tmp/systemregis_dummy.iso --instructions 40000000 --dual-sh2
```

Current bringup position:

- Master PC reaches Work RAM High frame-wait code at `0x06040226` with mounted dummy media in the latest 80M dual-SH2 run.
- The old Work RAM wait at `0x06028314..0x06028318` is passed after generated V-Blank-IN, V-Blank-OUT, and SMPC interrupt sources are modeled as accepted pulses.
- `GBR+0x90` / `0x06020240` is incremented by the V-Blank-OUT callback at `0x06028DB0`.
- SCU status ends at `0x00000000`; generated V-Blank and SMPC interrupt pulses are accepted and drained.
- `--disc` mounts a raw image through `RawDiscImage`; the dummy 256-sector image changes current-status to `CR1=0x2280`, `CR2=0x4101`, `CR3=0x0100`, `CR4=0x0096`.
- BIOS-observed mounted status-ready HIRQ mask `0x4658` lets the CD helper pass the old `0x00004C58/0x00004C04` blocker. Command `0x75` now raises `EFLS` and passes the old `0x000032EE` blocker.
- The current 80M run reports no unimplemented opcodes or bus faults after adding SH-2 `ROTCR Rn` and `NEG Rm,Rn`.
- Memory-map additions needed by the latest BIOS path: internal Backup RAM at `0x00180000..0x001FFFFF` with cache-through write-back, and Work RAM High mirror at `0x0C000000..0x0C0FFFFF`.
- The hot frame-wait loop at `0x06040226..0x0604022A` reads `GBR+0x90` / `0x06020240`; V-Blank callbacks are still accepted and increment that flag to `0x2E` in the 80M run.
- `--vblank-interval 100000` is available as a probe-only accelerator. It confirms the same dummy-disc path remains frame-paced at `0x06040226/0x06040228`, with faster V-Blank flag increments but no new CD command sequence.
- `Get TOC` now exposes a deterministic `0x00CC`-word single-data-track TOC through the host data port, including first-track, empty-track, A0/A1, and leadout entries.
- `Set Filter Range` plus `Get Sector Data` now exposes raw mounted-disc sectors through the same host data port. This is a deterministic selector shortcut, not yet full CD Block buffering/filter hardware.
- CD filesystem commands now have clean-room bringup coverage for `Change Directory`, `Read Directory`, `Get File System Scope`, `Get File Info`, and `Read File`.
- The current ISO9660 reader handles a primary volume descriptor and root-directory records, maps raw ISO LBA to Saturn FAD with the `150` sector bias, and exposes file id `2+` through CD Block file-info records.
- `.cue` images now mount through a minimal first-data-track CUE/BIN reader for 2352-byte raw sectors. `MODE1/2352` user data is read from byte offset `16` in each raw sector.
- Nights Into Dreams (Japan) at `/home/nichlas/roms/Saturn/NightsIntoDreams/` mounts through this path. The current 80M probe still reaches the same Work RAM High frame-wait loop as dummy media, so the next blocker is CD Block boot/status behavior before the BIOS asks for TOC or files.
- CD Block `Get Hardware Info` now matches the Mednafen-observed response shape used as a GPL behavioral oracle: `CR2=0x0002`, `CR4=0x0600`.
- Minimal auth status support detects `SEGA SEGASATURN ` in sector 0 as auth type `0x04` and exposes it through command `0xE1`. Nights Into Dreams reports `auth type: 0x04`, but the BIOS still polls current status only (`0x00`) in the 80M probe.
- `Get Current Status` command responses now omit the periodic bit (`0x0280` mounted standby), while periodic reads still report `0x2280`. This distinction moves Nights into CD setup commands before the next frame-loop blocker.
- Current setup-command coverage includes `Init` (`0x04`), `Reset Selector` (`0x48`), `Set Sector Length` (`0x60`), and `Get Copy Error` (`0x67`), implemented as register-level clean-room behavior from Mednafen-observed command semantics.

Next likely reference target:

- Real-disc boot gaps: selector/filter/buffer side effects after CD init, richer status transitions while BIOS polls current status, multi-directory ISO9660 behavior, and any transfer semantics proven by a bootable Saturn image.
- BIOS Work RAM High routines around `0x06040000..0x06040240`, `0x06040B70..0x06040C20`, `0x06041460..0x060414B0`, and `0x060422A0..0x060425D0`, from sources with clear redistribution terms before vendoring.
