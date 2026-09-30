namespace NAPS2.Scan.Internal;

/// <summary>
/// FOPA: per page metadata a driver can report about the paper itself, as opposed to the image.
/// Only TWAIN fills this in today; the other drivers pass null and the caller falls back to
/// counting images.
/// </summary>
/// <param name="SheetNumber">The physical sheet number (TWEI_PAPERCOUNT), 0 when unknown.</param>
/// <param name="PageSide">Which side of the sheet the image came from (TWEI_PAGESIDE).</param>
/// <param name="PatchCode">The patch code the driver read on the page (TWEI_PATCHCODE), null when none.</param>
/// <param name="PrinterText">What the imprinter printed on the sheet (TWEI_PRINTERTEXT), null when nothing.</param>
internal record ScanPageMetadata(int SheetNumber, PageSide PageSide, string? PatchCode = null, string? PrinterText = null);
