namespace SystemRegisIII.Core.Tools.TraceViewer;

public interface ITraceEventSink
{
    void Write(TraceEvent traceEvent);
}
