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
  - In this repo's current SCU mirror mapping, cache-through `0x25FE00A0/0x25FE00A4` appears at device offsets `0x0E00A0` and `0x0E00A4`.

### SMPC User's Manual

- URL: https://antime.kapsi.fi/sega/files/ST-169-R1-072694.pdf
- License status: marked SEGA Confidential; do not vendor.
- Useful clean-room facts:
  - SMPC status flag indicates command busy/completed.
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
  - BIOS VBlank interrupt activity currently writes CD Block command `0x00`, which this repo models as current-status response.

## Current BIOS Bringup Evidence

Verified with:

```text
dotnet run --project src/SystemRegisIII.Cli/SystemRegisIII.Cli.csproj -- run --bios "bios/Sega Saturn BIOS (J) (1.01).zip" --instructions 40000000 --dual-sh2
```

Current hot loop:

- Master PC loops at `0x06028314..0x06028318`.
- `GBR=0x06020000`.
- `MOV.L @(0x90,GBR),R0` reads `0x06020240`.
- BIOS loops while `[0x06020240] == R4`.
- Last observed value is `0x00000000`.

Next likely reference target:

- CD block command/status details from a source with clear redistribution terms before vendoring.
