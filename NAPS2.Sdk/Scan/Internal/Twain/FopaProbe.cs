#if !MAC
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// FOPA PoC: azt meri, hogy a NAPS2 sajat TWAIN sessionjebol elerheto-e az oldalankenti
/// metaadat (TWEI_PAPERCOUNT, TWEI_PAGESIDE), es hat-e a capability-beallitas.
/// A worker process-ben fut, ezert fajlba ir: a fo folyamat logja ide nem lat el.
/// Ha a meres sikeres, ez a kod a protoba es a PostProcessingData-ba kerul at.
/// </summary>
internal static class FopaProbe
{
    private static readonly string LogPath =
        Environment.GetEnvironmentVariable("FOPA_PROBE_LOG") ?? @"C:\fopa-probe.log";

    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // a meres nem allithatja meg a szkennelest
        }
    }

    public static void ReadExtImageInfo(DataTransferredEventArgs e)
    {
        try
        {
            var infos = e.GetExtImageInfo(ExtendedImageInfo.PaperCount, ExtendedImageInfo.PageSide).ToList();
            var parts = new List<string>();
            foreach (var info in infos)
            {
                var values = info.ReturnCode == ReturnCode.Success
                    ? string.Join(",", info.ReadValues())
                    : $"(rc={info.ReturnCode})";
                parts.Add($"{info.InfoID}={values}");
            }
            Log(parts.Count > 0 ? "ExtImageInfo: " + string.Join("  ", parts) : "ExtImageInfo: ures valasz");
        }
        catch (Exception ex)
        {
            Log($"ExtImageInfo HIBA: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
