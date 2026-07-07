namespace SystemRegisIII.Core.Core.Bus;

public readonly record struct BusAccess(uint Address, int SizeBytes, uint Value, bool IsWrite, string DeviceName);
