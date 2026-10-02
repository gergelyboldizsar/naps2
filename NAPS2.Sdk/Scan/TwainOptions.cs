namespace NAPS2.Scan;

/// <summary>
/// Scanning options specific to the TWAIN driver.
/// </summary>
public class TwainOptions
{
    /// <summary>
    /// The DSM version of TWAIN to use. Drivers/Windows usually come bundled with an Old version, while NAPS2 itself
    /// provides a New version. This is the most common thing to try changing if you have compatibility issues. You
    /// can also use NewX64 to access 64-bit TWAIN drivers but usually they don't exist, everything is generally 32-bit. 
    /// </summary>
    public TwainDsm Dsm { get; set; }

    /// <summary>
    /// The transfer mode used for TWAIN, either Native or Memory. By default Memory is used.
    /// </summary>
    public TwainTransferMode TransferMode { get; set; }

    /// <summary>
    /// Whether to show the TWAIN progress UI. This only matters when ScanOptions.UseNativeUI is false (otherwise the
    /// full UI is shown regardless).
    /// </summary>
    public bool ShowProgress { get; set; }

    /// <summary>
    /// Whether to include include devices that start with "WIA-" in GetDeviceList.
    /// Windows makes WIA devices available to TWAIN applications through a translation layer.
    /// By default they are excluded, since NAPS2 supports using WIA devices directly.
    /// </summary>
    public bool IncludeWiaDevices { get; set; }

    /// <summary>
    /// FOPA: stop the feed when the driver detects a double feed (CAP_DOUBLEFEEDDETECTION ultrasonic,
    /// CAP_DOUBLEFEEDDETECTIONRESPONSE = STOP). False leaves the driver's own setting. See
    /// TwainCaps.SupportsDoubleFeedStop.
    /// </summary>
    public bool StopOnDoubleFeed { get; set; }

    /// <summary>
    /// FOPA: driver side patch code recognition (ICAP_PATCHCODEDETECTIONENABLED). The code read on a
    /// page comes in PostProcessingData.PatchCode. See TwainCaps.SupportsPatchCodes.
    /// </summary>
    public bool DetectPatchCodes { get; set; }

    /// <summary>
    /// FOPA: text for the imprinter to print on every sheet (CAP_PRINTER*); null leaves the imprinter
    /// as the driver has it. What was printed comes in PostProcessingData.PrinterText. See
    /// TwainCaps.SupportsImprinter.
    /// </summary>
    public string? ImprinterText { get; set; }

    /// <summary>
    /// FOPA: the driver's own settings (DAT_CUSTOMDSDATA), as captured with CaptureDriverSettings.
    /// Applied before every other setting, so the options above still win. The blob belongs to one
    /// driver and model; applied to another it may be refused or misread.
    /// </summary>
    public byte[]? DriverSettings { get; set; }

    /// <summary>
    /// FOPA: instead of scanning, show the driver's settings dialog only (MSG_ENABLEDSUIONLY) and
    /// report what was set there through ScanController.DriverSettingsCaptured. No image is produced.
    /// Needs the TWAIN worker or an in-process TWAIN session. See TwainCaps.SupportsDriverSettings.
    /// With UseNativeUI the scan runs through the driver's dialog and the settings are reported after
    /// it: a driver may not keep what its settings-only dialog was given (PaperStream IP does not).
    /// </summary>
    public bool CaptureDriverSettings { get; set; }

    /// <summary>
    /// FOPA: the JPEG quality (1-100) the driver compresses with in File transfer mode.
    /// </summary>
    public int FileJpegQuality { get; set; } = 85;
}

/// <summary>
/// The data source manager (DSM) to use for TWAIN.
/// </summary>
public enum TwainDsm
{
    /// <summary>
    /// The modern 32-bit twaindsm.dll. Recommended.
    /// </summary>
    New,

    /// <summary>
    /// The modern 64-bit twaindsm.dll. Choose this if you want to use a 64-bit TWAIN data source.
    /// </summary>
    NewX64,

    /// <summary>
    /// The old 32-bit twain32.dll. Some data sources have compatibility issues with the newer DSM.
    /// </summary>
    Old
}

/// <summary>
/// The transfer mode to use for TWAIN.
/// </summary>
public enum TwainTransferMode
{
    /// <summary>
    /// Transfers the image using the recommended mode. Usually this is Memory, but some scanner drivers have known bugs
    /// with that mode, and where we can detect that we use Native instead.
    /// </summary>
    Default,

    /// <summary>
    /// Transfers the image in strips.
    /// </summary>
    Memory,

    /// <summary>
    /// Transfers the entire image at once. This may fail with very high-resolution images if they exceed the memory
    /// limits of the 32-bit worker.
    /// </summary>
    Native,

    /// <summary>
    /// FOPA: the driver writes each image to a file, compressed on its side: JPEG in colour and grayscale
    /// (TwainOptions.FileJpegQuality), Group 4 TIFF in black and white. Less data through the worker, and no
    /// uncompressed bitmap in its memory.
    /// </summary>
    File
}