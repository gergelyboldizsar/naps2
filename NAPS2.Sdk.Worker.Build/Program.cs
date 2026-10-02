using NAPS2.Images.Gdi;
using NAPS2.Remoting.Worker;
using NAPS2.Scan;

namespace NAPS2.Sdk.Worker;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public class Program
{
    public static async Task Main()
    {
        var scanningContext = new ScanningContext(new GdiImageContext());
        // FOPA: the worker logs nothing otherwise, and a TWAIN problem in it stays invisible.
        if (Environment.GetEnvironmentVariable("NAPS2_WORKER_LOG") is { Length: > 0 } logPath)
        {
            scanningContext.Logger = new FileLogger(logPath);
        }
        await WorkerServer.Run(scanningContext);
    }
}