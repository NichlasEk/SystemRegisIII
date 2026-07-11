namespace SystemRegisIII.Core.Core.CdBlock;

public readonly record struct CdTrackInfo(byte Number, byte ControlAdr, uint Fad);

public interface IDiscTableOfContents
{
    IReadOnlyList<CdTrackInfo> Tracks { get; }
    uint LeadoutFad { get; }
}
