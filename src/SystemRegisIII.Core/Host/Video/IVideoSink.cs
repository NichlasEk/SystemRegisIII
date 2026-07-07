namespace SystemRegisIII.Core.Host.Video;

public interface IVideoSink
{
    void Present(VideoFrame frame);
}
