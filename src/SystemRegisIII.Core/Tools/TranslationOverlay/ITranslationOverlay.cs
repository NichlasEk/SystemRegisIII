namespace SystemRegisIII.Core.Tools.TranslationOverlay;

public interface ITranslationOverlay
{
    bool TryGetOverlayText(out string text);
}
