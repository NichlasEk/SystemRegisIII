namespace SystemRegisIII.Core.Host.Video;

public readonly record struct VideoFrame(int Width, int Height, ReadOnlyMemory<uint> BgraPixels);
