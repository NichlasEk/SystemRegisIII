namespace SystemRegisIII.Core.Core;

public interface IClockedDevice
{
    string Name { get; }

    void Reset();

    void Step(SaturnCycleBudget budget);
}
