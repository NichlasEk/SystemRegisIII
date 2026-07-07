namespace SystemRegisIII.Core.Core.SaveState;

public interface ISaveStateSerializer
{
    void Save(Stream destination);

    void Load(Stream source);
}
