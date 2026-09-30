namespace NAPS2.Scan.Exceptions;

/// <summary>
/// FOPA: the scanner stopped on a double feed (TWCC_PAPERDOUBLEFEED).
/// </summary>
public class DeviceDoubleFeedException : DeviceException
{
    public DeviceDoubleFeedException() : base("The scanner stopped: two sheets were fed at once.")
    {
    }
}
