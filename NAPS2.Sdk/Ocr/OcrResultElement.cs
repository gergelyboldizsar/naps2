using System.Collections.Immutable;

namespace NAPS2.Ocr;

/// <summary>
/// A element in the result of an OCR request that represents a text segment.
/// </summary>
public record OcrResultElement(
    string Text,
    string LanguageCode,
    bool RightToLeft,
    (int x, int y, int w, int h) Bounds,
    int Baseline,
    int FontSize,
    ImmutableList<OcrResultElement> Children,
    // FOPA: a felismeres megbizhatosaga 0-100 skalan (a hOCR x_wconf mezojebol).
    // Sorok eseten a szavak atlaga. 0 = nincs adat (pl. olyan motortol, ami nem ad konfidenciat).
    int Confidence = 0);