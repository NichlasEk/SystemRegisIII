using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Smpc;

public sealed class SmpcRegisterBusDevice : IInspectableBusDevice
{
    private const uint CommandRegister = 0x1F;
    private const uint StatusFlagRegister = 0x63;
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Queue<byte> _recentCommands = new();

    public string Name => "SMPC Registers";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public byte LastCommand { get; private set; }
    public bool SlaveSh2Enabled { get; private set; }
    public IReadOnlyList<byte> RecentCommands => _recentCommands.ToArray();

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        // The bringup model completes SMPC commands immediately.
        return offset == StatusFlagRegister ? (byte)0 : (byte)0;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        if (offset != CommandRegister)
        {
            return;
        }

        LastCommand = value;
        _recentCommands.Enqueue(value);
        while (_recentCommands.Count > 16)
        {
            _recentCommands.Dequeue();
        }

        switch (value)
        {
            case 0x02:
                SlaveSh2Enabled = true;
                break;
            case 0x03:
                SlaveSh2Enabled = false;
                break;
        }
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();
}
