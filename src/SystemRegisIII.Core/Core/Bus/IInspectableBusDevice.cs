namespace SystemRegisIII.Core.Core.Bus;

public interface IInspectableBusDevice : IBusDevice
{
    long ReadCount { get; }
    long WriteCount { get; }
    uint? FirstReadOffset { get; }
    uint? LastReadOffset { get; }
    uint? FirstWriteOffset { get; }
    uint? LastWriteOffset { get; }

    IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count);

    IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count);
}
