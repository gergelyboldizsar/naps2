namespace NAPS2.Scan.Internal;

/// <summary>
/// FOPA: per page metadata a driver can report about the paper itself, as opposed to the image.
/// Only TWAIN fills this in today; the other drivers pass null and the caller falls back to
/// counting images.
/// </summary>
/// <param name="SheetNumber">The physical sheet number (TWEI_PAPERCOUNT), 0 when unknown.</param>
/// <param name="PageSide">Which side of the sheet the image came from (TWEI_PAGESIDE).</param>
internal record ScanPageMetadata(int SheetNumber, PageSide PageSide);
