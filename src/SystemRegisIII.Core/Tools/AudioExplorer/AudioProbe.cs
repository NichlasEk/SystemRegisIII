namespace SystemRegisIII.Core.Tools.AudioExplorer;

public sealed class AudioProbe
{
    public int SubmittedSampleFrames { get; private set; }

    public void ObserveFrames(int sampleFrames)
    {
        SubmittedSampleFrames += sampleFrames;
    }
}
