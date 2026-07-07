namespace SystemRegisIII.Core.Core.Memory;

public interface IMainMemory
{
    int SizeBytes { get; }

    Span<byte> Span { get; }

    void Clear();
}
