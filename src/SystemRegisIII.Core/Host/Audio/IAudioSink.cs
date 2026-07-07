namespace SystemRegisIII.Core.Host.Audio;

public interface IAudioSink
{
    void Submit(ReadOnlySpan<float> interleavedStereoSamples);
}
