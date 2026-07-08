namespace SystemRegisIII.Core.Core.CdBlock;

public enum CdBlockDriveStatus : byte
{
    Busy = 0x00,
    Pause = 0x01,
    Standby = 0x02,
    Play = 0x03,
    NoDisc = 0x07,
    Periodic = 0x20,
    Wait = 0x80,
}
