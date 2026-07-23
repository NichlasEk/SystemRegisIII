# SystemRegisIII Saturn bringup handoff

Date: 2026-07-19
Branch: `main`  
Baseline checkpoint: `24eba10` (`Record NiGHTS executable entry milestone`)

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

The immediate CMOK response for `04 0401` now follows the Mednafen-observed eight BIOS word polls. This preserves `R7=7` through the loaded program entry instead of the old zero value. Smoke coverage checks all seven early polls, the eighth-poll completion, and the later asynchronous reset completion.

The first loaded-program scan is no longer fed by a static VDP2 register stub. `EXTEN` reads latch a time-driven `HCNT`/`VCNT`, with the low-resolution 427-clock line length and Saturn horizontal-sync encoding used by the local Mednafen reference. The CLI and WaylandForge host both advance this timing once per interpreted master instruction. The run performs the expected 256 `EXTEN`/`HCNT` reads; after the final counter mix, SystemRegis and Mednafen follow the same structural path through `0x06002240`, `0x06002D8x`, BIOS `0x0000520x`, and the BIOS `0x000002Bx` poll. A 122M acceptance run reaches `PC=0x06004024`, inside the real NiGHTS executable whose entry is `0x06004000`.

The executable's large clear loop completes and issues SMPC clock-change command `0x0E`. That command now disables the slave SH-2, waits three VBlank entries, and sends the hardware NMI through vector 11 (`VBR+0x2C`); the BIOS vector is `0x20000534`, immediately after the old sleep resume PC `0x00000530`. The NMI handler's VDP2 `TVSTAT` HBlank poll is now driven by the live raster counter instead of a static zero. In the corrected 125M run the poll takes three reads rather than roughly 865,000, and execution advances to `PC=0x0602B532` in NiGHTS code.

A byte-for-byte comparison also ruled out the transferred program image: `0x06002000..0x0600213F` and the scan accumulator `R1=0xE21B7685` match Mednafen. Do not return to CD transfer corruption or an SH-2 cache workaround without new contradictory evidence.

The loop at `0x060111A8..0x060111B0` was not a deadlock. It compares the VBlank-maintained word at `0x060348EC` with the target `0x0082`; SystemRegis enters at `0x0001`, reaches `0x0083` at instruction 106,750,089, and exits normally. Mednafen enters the same wait at `0x0029`.

Read File now distinguishes a complete file from a full 200-sector partition. NiGHTS fills the partition, publishes FAD `0x017D`, and raises `BFUL` without the premature `EFLS` bit. The post-read HIRQ state consequently matches Mednafen (`0x0DDC` before the next command-completion bit).

## Visual status

There is no new visible screen yet. The latest frame dump still contains the same Sega/copyright frame:

- 11 VDP1 commands
- 8 drawable sprites
- 1,623 rendered pixels
- richest capture remains at instruction 89,700,000

The full 200-sector executable transfer now completes, the reset wait releases, clock change returns through NMI, and control advances into NiGHTS code at `0x0602B532`. The richest visible capture has not changed yet.

## Current blocker

The previous blockers were the missing periodic idle report after the file-info End Data Transfer and the missing asynchronous completion for `04 0401`. The six transferred file-info words already matched Mednafen exactly:

```text
0000,00B5,0006,D59C,0000,0000
```

The BIOS compares the response successfully, then rejects its control byte until bit `0x20` appears. Mednafen changes the byte from `0x01` to periodic Pause `0x21` after 283 polls. SystemRegis previously left it at `0x01` forever. A delayed periodic status at 38,000 master instructions now reproduces that transition and releases the expected `51,63,06` sequence.

Mednafen then completes the `04 0401` software reset asynchronously and raises `MPED|EFLS|ECPY|EHST|ESEL|CMOK` (`0x0BC1`). SystemRegis previously waited for eight explicit status polls that never occur on this path. The delayed completion releases the BIOS into the loaded program, and the clock-change NMI plus live `TVSTAT` now clears the later BIOS sleep. The current blocker is after `0x0602B532`; take the next focused trace there and determine whether it is finite initialization, a hardware wait, or the first new divergence.

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
- SMPC clock change completes after three VBlanks and delivers the BIOS NMI through vector 11.
- VDP2 `TVSTAT` exposes live HBlank/VBlank state from the raster counters.
- Both SH-2 DMA channels expose SAR/DAR/TCR/CHCR plus DMAOR and perform completed
  8/16/32/16-byte-unit transfers. NiGHTS now leaves its former `CHCR1=0x5601`
  wait at `0x0602B52C..0x0602B532` with copied data, `TCR=0`, and `TE=1`.
- The CLI can capture a post-file-info SH-2 trace, WRAM snapshot, focused return-slot activity, and an arbitrary instruction window.

The 123M-instruction acceptance run now advances far into the executable: it
services more than one thousand VBlank interrupts, writes VDP2 VRAM/CRAM, and
produces a richest VDP1 list with 8 drawable sprites in 11 commands. The next
deterministic blocker is later and separate from DMA: master SH-2 enters
`0x060069CE` with `R15=0x05FE0000`; its `STS.L PR,@-R15` then faults at the
unmapped `0x05FDFFFC`. The captured state also has `PR=0x060681F2`, so trace the
provenance of R15/PR immediately before that handler rather than widening the
SCU mapping. Bus-fault reports now include the full architectural CPU state.

The follow-up provenance probes locate the actual stack drain. At instruction
122,418,378, game code `MOV.L @(0x38,PC),R0` at `0x060040A6` deliberately loads
`R0=0x060069CE` from literal `0x06004188`. That function uses `BSRF R1` at
`0x060069D2` with `R1=0x00061818`, correctly targeting `0x060681EE`; the target
starts with `JSR @R0` and therefore calls `0x060069CE` again. Each recursive
entry saves PR and consumes four stack bytes until R15 crosses below Work RAM
High. Renesas' SH-2 definition and Mednafen both confirm the current BSRF base
(`instruction address + 4 + Rm`), so do not patch the branch by four bytes.
Compare the reference contents/execution at `0x06004188`, `0x060069CE`, and
`0x060681EE`, with cache/coherency or earlier relocation as the remaining
hypotheses. CLI options `--probe-r0 HEX` and `--probe-suspect-stack` now stop at
the first relevant register transition and report its producing instruction.

The later `060508F0` zero-opcode boundary is resolved. Slave BIOS, not the CD
block, cleared the master executable because the slave role bit was modeled in
the wrong internal-register byte. SH7095 BCR1 and the CPU-local cache-control
spaces are now modeled at their real addresses, including six-bit pseudo-LRU.

The next CDB boundary has also moved. Commands `52`/`53` now calculate and
return the actual host-transfer size, with delayed ESEL and partition-aware
sector accounting. For NiGHTS's one 2048-byte sector, `53` reports 1,024 words
and `61` exposes exactly those words while returning the reference Seek/DTREQ
position. Post-delete drive phases now follow the reference Seek-to-Busy-to-
Pause ordering, and command `50` reports the real 200-buffer geometry.

The first continuation exposed missing SH-2 `CMP/STR` (`264C`), which is now
implemented and smoke-tested. Late short Play requests also remain active until
their partition is drained, advance FAD per deleted sector, and enter Busy at
the final-sector boundary instead of completing on the old fixed timer. A clean
128M run has no bus fault or unimplemented opcode and completes six
`52,53,61,06,62` cycles.

The final-sector HIRQ boundary is now split into the two hardware-visible
phases seen in the local Mednafen trace. Final command `62` returns Busy/FAD
`00AF` with HIRQ `0B44`; the first HIRQ poll then publishes deferred
`CMOK|EHST`, allowing command `00` to run with HIRQ `0BC4`. A focused smoke
assertion locks that ordering. The old master timeout at `060683AA` is gone;
the first status command no longer falls into its dense Busy retry tail.

A CD-command occurrence trigger for the SH-2 differential trace and a focused
watch on `060662E0..060662EF` identified the missing event. BIOS routine
`0606F240..0606F280` rejects the response with `R0=-8` while the response-
control byte copied from CR1 is `00`; it accepts a periodic response whose
control byte carries bit `20`. One thousand master instructions after the final
sector drain, the CD block now publishes the byte-exact Play-end periodic Pause
report `2100,4101,0100,00AF`. It adds neither PEND nor SCDQ; the local reference
still has HIRQ `0BC4` before its first sector-count command, so the
already-latched SCDQ bit is preserved rather than newly generated here.

This releases the complete cleanup path. SystemRegis now advances from final
`62,00` through `50,51(count=0),00,48,00,44,42,46`, then reaches the next
reference Play request `1080,12BB,0080,000C`. Its subsequent `51,00,00` polling
at Busy/FAD `12BB` also matches the local Mednafen trace while the new long seek
is active. The remaining narrow discrepancy is the extra buffer-size command
`50` before the first `51`; Mednafen begins directly with `51`. Do not retune
the matched final HIRQ edge or revive early PEND/SCDQ. The next differential is
the task selection that adds that one `50`, followed by the long-seek completion
near FAD `12C7`.

The FAD-`12C7` differential has since isolated the first post-deletion status
edge. Mednafen keeps HIRQ at `0B44` for six BIOS word polls, raises EHST to
`0BC4` on poll seven, and raises CMOK to `0BC5` on poll fifteen. SystemRegis now
models both delayed edges and smoke covers the exact 7/15 timing. The 130.27M
run remains fault-free but still selects `62,00,00,51,...` instead of the
reference `62,00,51,00,48`.

The previously reported `06066EDC = 3` versus `12` difference was a trace-PC
pipeline alignment error: both cores load `3`. SH-2 displacement scaling also
corrects the scheduler operands to quantum `0606662C` and task accumulator
`06066EAC`, not `06066638`/`06066EB4`. At the first post-delete helper call the
reference reads `07E0 + 5800` and stores `5FE0`; SystemRegis reads `0800 + 0000`
and stores `0800`. A separate Mednafen WRAM write probe confirms `5FE0` is the
first accumulator write after the eighteenth command `62`, so the missing
`5800` predates the new post-delete HIRQ staging. Continue by tracing the
earlier lifecycle/reset of `06066EAC`; do not retune the matched HIRQ waveform
or add a command-specific task-selection shortcut.

That reset is now localized. After SystemRegis command `62` occurrence six,
the scheduler adds `0800`, then task destruction at `0606C3F2` clears
`06066EAC`; the task callback path later clears it again in the `RTS` delay
slot at `0606CC6E`. Both clears happen before occurrence seven and the long
Play completes. In the corresponding Mednafen interval, the accumulator is
retained as `5000`, grows to `5800`, then to `5FE0` at the next `62`, and is
only cleared afterward. Caller `0606B418` selects the callback/destructor path
from the lifecycle word at task offset `+0x34` (`06066ED8`): values zero and
six take the path, while other values skip it. The CLI now keeps bounded,
instruction-stamped watches for the quantum, accumulator, and this lifecycle
word. Continue by comparing the write that changes `06066ED8` and the
destructor caller around `0606BDD4..0606BDF6`; the divergence is one task
lifetime too early, not an arithmetic or HIRQ-waveform error.

## July 22 Multi-Sector Play-End Release

The first post-backup-wrapper differential was a CD HIRQ read at actual PC
`0606AD20`: Mednafen returned `0FD5`, while SystemRegis returned `0BC5`. The
missing bits were exactly PEND and SCDQ. An event probe in the local Mednafen
copy showed that the final sector of a multi-sector Play remains in Play for
one sector interval, then enters Pause and raises PEND; the following periodic
drive update raises SCDQ while PEND remains latched.

SystemRegis previously scheduled its long-play completion only while the drive
was still in Seek. Once the first buffered sector had moved it into Play, the
last sector deletion could never schedule the final Pause event. A drained
multi-sector Play now waits 140,000 master instructions, publishes periodic
Pause `2180` with PEND, and raises recurring SCDQ at 200,000-instruction
intervals until the next Play command. Smoke coverage verifies both timer
boundaries and PEND retention.

The 240M automatic acceptance is fault-free at master/slave PCs
`0606E5CA`/`06005FA0`. An SH-2 trace anchored at the unique backup-wrapper
return `0604D3AC` now reads HIRQ `0FD5` at both `060681F2` and `0606AD20`.
Twelve comparable CPU/register milestones through `0607048E` match the local
Mednafen trace, excluding the already-known historical MACL difference. The
original 3,100-state SystemRegis trace ended during the following directory
scan and made that finite scan look divergent. The extended trace described
below reaches the `PRGMOVIE.PRS` directory entry and rules that hypothesis out.

## July 23 Selector Command Completion Phases

The first real differential after the directory scan was Set Filter Mode
command `44`. Mednafen acknowledges the old CMOK/ESEL bits from HIRQ
`0FD5 -> 0F94`, completes CMOK after eight unsuccessful BIOS word polls, and
therefore reaches actual PC `06068380` with HIRQ snapshot `R6=0F95` and poll
counter `R7=8`. ESEL is a distinct later event after the command's additional
96-clock selector phase. SystemRegis previously raised CMOK and ESEL
immediately, producing `R6=0FD5`, `R7=0`.

Commands `40,42,44,46` now delay CMOK for eighteen byte register reads (eight
unsuccessful word polls followed by the successful ninth read), then schedule
ESEL as a separate 96-instruction bringup event. The ESEL event is independent
of the generic command-midpoint state, so an intervening status command cannot
discard it. Smoke coverage pins the CMOK/ESEL ordering for both Set Filter
Range and Set Filter Mode, including the intervening status-command case.

The clean Release 240M acceptance is fault-free, writes all 30,000 requested
trace states, and ends at valid master/slave PCs `0606FA92`/`06005FA0`. At
actual PC `06068380`, SystemRegis now exactly matches the reference
`R6=00000F95`, `R7=00000008`. A sequential comparison matches 131 reference
CPU/register milestones after the unique `0604D3AC` anchor (ignoring the
already-known MACL history). The next concrete difference is reference
sequence 3201 at actual PC `0606F2F6`: Mednafen has `R1=01000B16`, while
SystemRegis has `R1=01000B26`; the remaining compared registers match there.

This still does not produce a new visible boot/game frame. The richest complete
VDP1 list remains the eight-drawable, eleven-command capture from instruction
89.7M. Continue from the `R1` provenance immediately before `0606F2F6`; do not
return to the disproven directory-scan hypothesis or retune the now-matched
selector HIRQ waveform.

## July 23 CD Pickup and Play-Argument Provenance

The `R1` difference at actual PC `0606F2F6` is the packed CD response
`CR3:CR4`, not an SH-2 arithmetic difference. The reference reads
`0180,4101,0100,0B16`; SystemRegis reads
`0180,4101,0100,0B26`.

The CLI now records a bounded CD pickup timeline whenever the internal FAD
changes. Each record includes instruction index, master PC, old/new FAD, the
complete last-command CR input, and the current response. This separates
command-argument provenance from later asynchronous sector publication without
requiring a huge architectural trace.

A clean 240M pickup run proves that the CD sector timer is not adding the same
16 sectors twice. SystemRegis issues Play as
`1080,0B16,0080,0010` at instruction 155,221,236, then publishes exactly
sixteen sequential pickup changes from `0B16` through `0B26`. The existing
Mednafen reference for this workload issues
`1080,0B06,0080,0010` and later reports `0B16`. The extra `0x10` is therefore
already present in the SystemRegis Play start argument; `0B26` is its correct
modeled end, not a second increment inside `PublishNextLongPlaySector`.

This moves the next differential earlier than the selector response. Trace the
producer of command-7 Play CR2 immediately before the write at master PC
`060683CE`, and compare it with the Mednafen `0B06` command construction. Do
not clamp the CD response to `0B16`, subtract the sector count inside
`PlayDisc`, or remove continuous long-play buffering: those would hide the
upstream argument divergence and regress the already verified streamed-sector
workloads.

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
  --instructions 128000000 \
  --summary-only
```

The detailed historical log remains in `docs/saturn-bringup-handoff-2026-07-09.md`.

## Workspace state

At handoff creation, `main` was synchronized with `origin/main` and had no unrelated changes. The local Mednafen reference lab remains under ignored `local/mednafen-lab` and must not be committed.
