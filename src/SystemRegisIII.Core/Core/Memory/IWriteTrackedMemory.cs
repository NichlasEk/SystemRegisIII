namespace SystemRegisIII.Core.Core.Memory;

public interface IWriteTrackedMemory
{
    long WriteCount { get; }
    uint? FirstWriteOffset { get; }
    uint? LastWriteOffset { get; }
}
