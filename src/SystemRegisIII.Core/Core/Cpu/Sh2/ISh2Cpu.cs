using SystemRegisIII.Core.Core;

namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public interface ISh2Cpu : IClockedDevice
{
    Sh2Registers Registers { get; }
}
