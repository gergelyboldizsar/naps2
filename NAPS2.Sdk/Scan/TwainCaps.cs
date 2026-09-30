namespace NAPS2.Scan;

/// <summary>
/// FOPA: what the TWAIN driver can do beyond the common capabilities, for the TwainOptions that use it.
/// </summary>
public class TwainCaps
{
    /// <summary>The driver can stop the feed on a double feed (CAP_DOUBLEFEEDDETECTIONRESPONSE has STOP).</summary>
    public bool SupportsDoubleFeedStop { get; init; }

    /// <summary>The driver recognises patch codes (ICAP_PATCHCODEDETECTIONENABLED).</summary>
    public bool SupportsPatchCodes { get; init; }

    /// <summary>An imprinter is fitted and the driver drives it (CAP_PRINTERENABLED).</summary>
    public bool SupportsImprinter { get; init; }

    /// <summary>The driver saves and restores its own settings (CAP_CUSTOMDSDATA).</summary>
    public bool SupportsDriverSettings { get; init; }

    /// <summary>The driver compresses to JPEG in File transfer mode (ICAP_XFERMECH has FILE, ICAP_COMPRESSION has JPEG).</summary>
    public bool SupportsFileTransfer { get; init; }
}
