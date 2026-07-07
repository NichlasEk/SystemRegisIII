using SystemRegisIII.Core.Core;

namespace SystemRegisIII.Core.Host.Timing;

public interface IHostClock
{
    SaturnCycleBudget GetFrameBudget();
}
