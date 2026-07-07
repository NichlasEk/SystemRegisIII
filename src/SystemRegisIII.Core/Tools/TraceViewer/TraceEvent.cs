namespace SystemRegisIII.Core.Tools.TraceViewer;

public readonly record struct TraceEvent(string Source, long Cycle, string Message);
