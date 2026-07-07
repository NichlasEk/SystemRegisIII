namespace SystemRegisIII.Core.Tools.TraceViewer;

public sealed class RingTraceEventSink(int capacity) : ITraceEventSink
{
    private readonly Queue<TraceEvent> _events = new(capacity);

    public IReadOnlyCollection<TraceEvent> Events => _events;

    public void Write(TraceEvent traceEvent)
    {
        if (_events.Count == capacity)
        {
            _events.Dequeue();
        }

        _events.Enqueue(traceEvent);
    }
}
