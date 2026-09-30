using NAPS2.Scan.Internal;

namespace NAPS2.Scan;

internal class ScanEvents : IScanEvents
{
    public static readonly IScanEvents Stub = new ScanEvents(() => { }, _ => { }, (_, _) => { });

    private readonly Action _pageStartCallback;
    private readonly Action<double> _pageProgressCallback;
    private readonly Action<string?, string?> _deviceUriChangedCallback;
    private readonly Action<byte[]>? _driverSettingsCallback;

    public ScanEvents(Action pageStartCallback, Action<double> pageProgressCallback,
        Action<string?, string?> deviceUriChangedCallback, Action<byte[]>? driverSettingsCallback = null)
    {
        _pageStartCallback = pageStartCallback;
        _pageProgressCallback = pageProgressCallback;
        _deviceUriChangedCallback = deviceUriChangedCallback;
        _driverSettingsCallback = driverSettingsCallback;
    }

    public void DriverSettingsCaptured(byte[] data)
    {
        _driverSettingsCallback?.Invoke(data);
    }

    public void PageStart()
    {
        _pageStartCallback();
    }

    public void PageProgress(double progress)
    {
        _pageProgressCallback(progress);
    }

    public void DeviceUriChanged(string? iconUri, string? connectionUri)
    {
        _deviceUriChangedCallback(iconUri, connectionUri);
    }
}