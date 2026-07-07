namespace SystemRegisIII.Core.Host.Input;

[Flags]
public enum SaturnInputState : ushort
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Start = 1 << 4,
    A = 1 << 5,
    B = 1 << 6,
    C = 1 << 7,
    X = 1 << 8,
    Y = 1 << 9,
    Z = 1 << 10,
    L = 1 << 11,
    R = 1 << 12,
}
