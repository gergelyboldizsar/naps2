namespace NAPS2.Scan.Internal;

internal class PostProcessingContext
{
    /// <summary>
    /// Stores the scan page number for determining whether we're scanning the front or back side of a page in a duplex
    /// scan.
    /// </summary>
    public int PageNumber { get; set; }

    /// <summary>
    /// FOPA: the physical sheet number as reported by the driver (TWEI_PAPERCOUNT), 0 when the
    /// driver does not report it. Unlike PageNumber this does not drift when a page is lost.
    /// </summary>
    public int SheetNumber { get; set; }

    /// <summary>
    /// FOPA: the real page side as reported by the driver (TWEI_PAGESIDE). Unknown means the
    /// caller has to fall back to image parity.
    /// </summary>
    public PageSide PageSide { get; set; }

    // TODO: Consider renaming this (to RenderedFilePath?), and make sure it works correctly (e.g. across normal/worker/network scans)
    /// <summary>
    /// Stores the path to an image file on disk with the scanned image (after some transformations) for use in
    /// post-scan OCR. This is an optimization to avoid having to immediately re-render the image.
    /// </summary>
    public string? TempPath { get; set; }
}