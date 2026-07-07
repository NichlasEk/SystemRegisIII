namespace SystemRegisIII.Core.Host.Input;

public interface IInputSource
{
    SaturnInputState Poll();
}
