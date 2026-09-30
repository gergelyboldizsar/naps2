#if !MAC
using System.Runtime.InteropServices;
using System.Threading;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using NAPS2.Remoting.Worker;
using NAPS2.Scan.Exceptions;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Interfaces with the native NTwain TwainSession to perform an actual scan. The raw scanned data is propagated via the
/// ITwainEvents interface. This logic involves quite a bit of complicated state management related to the Twain spec.
/// https://twain.org/wp-content/uploads/2015/05/TWAIN-2.3-Specification.pdf
/// </summary>
internal class TwainScanRunner
{
    private readonly ILogger _logger;
    private readonly TwainDsm _dsm;
    private readonly ScanOptions _options;
    private readonly CancellationToken _cancelToken;
    private readonly ITwainEvents _twainEvents;
    private readonly TwainHandleManager _handleManager;
    private readonly TwainSession _session;
    private readonly TaskCompletionSource<bool> _tcs;
    private readonly TaskCompletionSource<bool> _sourceDisabledTcs;
    private DataSource? _source;

    // FOPA: File transfer mode: the format the driver writes, and a new file name for every image.
    private FileFormat _fileFormat;
    private int _fileIndex;

    public TwainScanRunner(ILogger logger, TWIdentity twainAppId, TwainDsm dsm, ScanOptions options,
        CancellationToken cancelToken, ITwainEvents twainEvents)
    {
        _logger = logger;
        _dsm = dsm;
        _options = options;
        _cancelToken = cancelToken;
        _twainEvents = twainEvents;

        _handleManager = TwainHandleManager.Factory();
        PlatformInfo.Current.PreferNewDSM = dsm != TwainDsm.Old;
        _logger.LogDebug($"Using TWAIN DSM: {PlatformInfo.Current.ExpectedDsmPath}");
        _session = new TwainSession(twainAppId);
        _session.TransferReady += TransferReady;
        _session.DataTransferred += DataTransferred;
        _session.TransferCanceled += TransferCanceled;
        _session.TransferError += TransferError;
        _session.SourceDisabled += SourceDisabled;
        _session.StateChanged += StateChanged;
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sourceDisabledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task Run()
    {
        _handleManager.Invoker.InvokeDispatch(Init);
        return _tcs.Task;
    }

    private void Init()
    {
        try
        {
            _logger.LogDebug("NAPS2.TW - Opening session");
            bool capture = _options.TwainOptions.CaptureDriverSettings;
            bool useNativeUi = _options.UseNativeUI || _options.TwainOptions.ShowProgress || capture;
            var rc = _session.Open(_handleManager.CreateMessageLoopHook(_options.DialogParent, useNativeUi));
            if (rc != ReturnCode.Success)
            {
                throw new DeviceException($"TWAIN session open error: {rc}");
            }

            _logger.LogDebug("NAPS2.TW - Finding source");
            _source = _session.FirstOrDefault(x => x.Name == _options.Device!.ID);
            if (_source == null)
            {
                throw new DeviceNotFoundException();
            }

            _logger.LogDebug("NAPS2.TW - Opening source");
            _logger.LogDebug(
                "NAPS2.TW - Name: {Name}; Manu: {Manu}; Family: {Family}; Version: {Version}; Protocol: {Protocol}",
                _source.Name, _source.Manufacturer, _source.ProductFamily, _source.Version, _source.ProtocolVersion);
            rc = _source.Open();
            if (rc != ReturnCode.Success)
            {
                throw GetExceptionForStatus(_session.GetStatus());
            }

            _logger.LogDebug("NAPS2.TW - Configuring source");
            ConfigureSource(_source);

            // FOPA: an empty feeder is refused here, before MSG_ENABLEDS. PaperStream IP on the
            // fi-7700 otherwise shows its own "no document" dialog, and a cancel there ends the
            // session as if it had succeeded with no pages (measured 2026-09-23). CAP_FEEDERLOADED
            // is reliable on this driver: it drops from 1 to 0 as the tray empties.
            if (!capture && _options.PaperSource is PaperSource.Feeder or PaperSource.Duplex &&
                _source.Capabilities.CapFeederLoaded.IsSupported &&
                _source.Capabilities.CapFeederLoaded.GetCurrent() == BoolType.False)
            {
                _logger.LogDebug("NAPS2.TW - FOPA feeder empty, not enabling the source");
                throw new DeviceFeederEmptyException();
            }

            _logger.LogDebug("NAPS2.TW - Enabling source");
            // FOPA: CaptureDriverSettings shows the driver's settings dialog only, without a scan.
            var ui = capture ? SourceEnableMode.ShowUIOnly
                : _options.UseNativeUI ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
            var enableHandle = _handleManager.GetEnableHandle(_options.DialogParent, useNativeUi);
            // Note that according to the twain spec, on Windows it is recommended to set the modal parameter to false
            rc = _source.Enable(ui, false, enableHandle);
            if (rc != ReturnCode.Success)
            {
                throw GetExceptionForStatus(_source.GetStatus());
            }

            _cancelToken.Register(() => _handleManager.Invoker.Invoke(FinishWithCancellation));
            _sourceDisabledTcs.Task.ContinueWith(_ => _handleManager.Invoker.Invoke(() =>
                {
                    // FOPA: the dialog closed with the source still open, so its settings can be read now.
                    if (capture)
                    {
                        ReportDriverSettings(_source);
                    }
                    FinishWithCompletion();
                }))
                .AssertNoAwait();
        }
        catch (Exception ex)
        {
            FinishWithError(ex);
        }
    }

    private void FinishWithCancellation()
    {
        _logger.LogDebug("NAPS2.TW - Finishing with cancellation");
        if (_session.State != 5)
        {
            // If we're in state 6 or 7, this will abort the ongoing transfer via ForceStepDown.
            // If we're in state 4 or lower, then we're not transferring and this will just clean up the source/session.
            UnloadTwain();
            _tcs.TrySetResult(false);
        }
        else
        {
            // If we're in state 5, we can just wait for the TransferReady event and "naturally" cancel the transfer.
            // (Or if we're in state 5 in the process of finishing all transfers, then we don't need to cancel anyway.)
            // This will result in FinishWithCompletion being called when the source disables itself.
            // The alternative of calling ForceStepDown from state 5 seems to produce an error message from the scanner.
            _logger.LogDebug("NAPS2.TW - Will cancel via TransferReady");
        }
    }

    private void FinishWithError(Exception ex)
    {
        _logger.LogDebug(ex, "NAPS2.TW - Finishing with error");
        // If we're in state 5 or higher, we'll call ForceStepDown, which could potentially produce additional errors,
        // but what alternative is there?
        // If we're in state 4 or lower, this will just clean up the source/session.
        UnloadTwain();
        _tcs.TrySetException(ex);
    }

    private void FinishWithCompletion()
    {
        _logger.LogDebug("NAPS2.TW - Finishing with completion");
        // At this point we should be in state 4 and this will clean up the source/session.
        UnloadTwain();
        _tcs.TrySetResult(true);
    }

    private void UnloadTwain()
    {
        try
        {
            if (_session.State > 4)
            {
                // If a transfer is initialized or in progress, this will abort it and also clean up the source/session.
                _session.ForceStepDown(2);
                return;
            }
            // If a transfer isn't in progress, we just clean up the source/session as needed.
            if (_session.State == 4)
            {
                _source!.Close();
            }
            if (_session.State >= 3)
            {
                _session.Close();
            }
        }
        finally
        {
            _handleManager.Dispose();
        }
    }

    private void StateChanged(object? sender, EventArgs e)
    {
        _logger.LogDebug($"NAPS2.TW - StateChanged (to {_session.State})");
    }

    private void SourceDisabled(object? sender, EventArgs e)
    {
        _logger.LogDebug("NAPS2.TW - SourceDisabled");
        _sourceDisabledTcs.TrySetResult(true);
    }

    private void TransferCanceled(object? sender, TransferCanceledEventArgs e)
    {
        _logger.LogDebug("NAPS2.TW - TransferCanceled");
        _twainEvents.TransferCanceled(new TwainTransferCanceled());
    }

    private void TransferError(object? sender, TransferErrorEventArgs e)
    {
        _logger.LogDebug("NAPS2.TW - TransferError");
        FinishWithError(e.Exception ?? GetExceptionForStatus(e.SourceStatus));
    }

    private Exception GetExceptionForStatus(TWStatus status)
    {
        switch (status.ConditionCode)
        {
            case ConditionCode.OperationError:
                // This means the scanner has already shown the user an error message, so we don't need to show another.
                // TODO: The spec says that if CAP_INDICATORS is false with NO_UI, we should still display the error to
                // the user, but with my test scanners that seems unnecessary so for now I'm not showing the error
                // regardless.
                return new AlreadyHandledDriverException();
            case ConditionCode.PaperJam:
                return new DevicePaperJamException();
            case ConditionCode.PaperDoubleFeed:
                // FOPA: otherwise a generic "TWAIN error", and the caller cannot tell it apart.
                return new DeviceDoubleFeedException();
            case ConditionCode.CheckDeviceOnline when _session.State <= 3:
                return new DeviceOfflineException();
            case ConditionCode.CheckDeviceOnline when _session.State >= 4:
                return new DeviceCommunicationException();
            default:
                return new DeviceException($"TWAIN error: {status.ConditionCode}");
        }
    }

    /// <summary>
    /// FOPA: reads the physical sheet number and the real page side from the driver
    /// (TWEI_PAPERCOUNT, TWEI_PAGESIDE). Both are optional: a driver that does not report them
    /// yields zeros, and the caller falls back to counting images.
    /// </summary>
    private TwainPageMetadata ReadPageMetadata(DataTransferredEventArgs e)
    {
        var metadata = new TwainPageMetadata();
        int? rawPageSide = null;
        try
        {
            // Only the infos asked for: some drivers fail the whole call on one they do not know.
            var ids = new List<ExtendedImageInfo> { ExtendedImageInfo.PaperCount, ExtendedImageInfo.PageSide };
            if (_options.TwainOptions.DetectPatchCodes) ids.Add(ExtendedImageInfo.PatchCode);
            if (_options.TwainOptions.ImprinterText != null) ids.Add(ExtendedImageInfo.PrinterText);
            foreach (var info in e.GetExtImageInfo(ids.ToArray()))
            {
                if (info.ReturnCode != ReturnCode.Success)
                {
                    continue;
                }
                var value = info.ReadValues().FirstOrDefault();
                if (value == null)
                {
                    continue;
                }
                if (info.InfoID == ExtendedImageInfo.PaperCount)
                {
                    metadata.SheetNumber = Convert.ToInt32(value);
                }
                else if (info.InfoID == ExtendedImageInfo.PatchCode)
                {
                    // TWPCH_PATCH1..4 = 0..3, TWPCH_PATCH6 = 4, TWPCH_PATCHT = 5
                    metadata.PatchCode = Convert.ToInt32(value) switch
                    {
                        0 => "1",
                        1 => "2",
                        2 => "3",
                        3 => "4",
                        4 => "6",
                        5 => "T",
                        _ => ""
                    };
                }
                else if (info.InfoID == ExtendedImageInfo.PrinterText)
                {
                    metadata.PrinterText = Convert.ToString(value) ?? "";
                }
                else if (info.InfoID == ExtendedImageInfo.PageSide)
                {
                    // The TWAIN spec says TWPS_FRONT = 0, TWPS_BACK = 1, but PaperStream IP on the
                    // fi-7700 reports 1 for the front and 2 for the back (measured 2026-09-17 and
                    // again 2026-09-18). We carry 1/2 and keep 0 for "the driver said nothing",
                    // and we log the raw value so a different driver stays diagnosable.
                    var raw = Convert.ToInt32(value);
                    rawPageSide = raw;
                    metadata.PageSide = raw switch
                    {
                        1 => 1,
                        2 => 2,
                        _ => 0
                    };
                }
            }
        }
        catch (Exception ex)
        {
            // Metadata is an optimization, never a reason to fail a scan.
            _logger.LogDebug(ex, "NAPS2.TW - FOPA could not read ExtImageInfo");
        }
        _logger.LogDebug(
            "NAPS2.TW - FOPA page metadata: sheet={sheet} side={side} (raw side={raw}) patch={patch} printed={printed}",
            metadata.SheetNumber, metadata.PageSide, rawPageSide, metadata.PatchCode, metadata.PrinterText);
        return metadata;
    }

    private void DataTransferred(object? sender, DataTransferredEventArgs e)
    {
        _logger.LogDebug("NAPS2.TW - DataTransferred");
        // FOPA: the metadata has to go out before the image events, so the receiving side can
        // attach it to the image it is about to emit.
        _twainEvents.PageMetadata(ReadPageMetadata(e));
        try
        {
            if (_options.TwainOptions.TransferMode == TwainTransferMode.Memory && e.MemoryData == null)
            {
                _logger.LogDebug("NAPS2.TW - Expected memory transfer, but got native transfer?");
            }
            if (_options.TwainOptions.TransferMode == TwainTransferMode.File)
            {
                // FOPA: the driver wrote a compressed file; it goes on as it is, the receiver decodes it.
                var path = e.FileDataPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    throw new DeviceException($"TWAIN file transfer: the driver wrote no file (image {_fileIndex}).");
                }
                try
                {
                    _twainEvents.NativeImageTransferred(new TwainNativeImage
                    {
                        Buffer = ByteString.CopyFrom(File.ReadAllBytes(path))
                    });
                }
                finally
                {
                    File.Delete(path);
                }
            }
            else if (e.MemoryData != null)
            {
                _twainEvents.MemoryBufferTransferred(ToMemoryBuffer(e.MemoryData, e.MemoryInfo));
            }
            else
            {
                _twainEvents.NativeImageTransferred(new TwainNativeImage
                {
                    Buffer = ByteString.FromStream(e.GetNativeImageStream())
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending TWAIN data transfer event");
        }
    }

    private void TransferReady(object? sender, TransferReadyEventArgs e)
    {
        _logger.LogDebug("NAPS2.TW - TransferReady");
        try
        {
            var pageStart = new TwainPageStart();
            if (_options.TwainOptions.TransferMode == TwainTransferMode.Memory)
            {
                pageStart.ImageData = ToImageData(e.PendingImageInfo);
            }
            _twainEvents.PageStart(pageStart);
            if (_options.TwainOptions.TransferMode == TwainTransferMode.File)
            {
                // FOPA: a fresh name before every image, or the driver overwrites the previous one.
                var path = Path.Combine(Path.GetTempPath(),
                    $"naps2-fopa-{Environment.ProcessId}-{++_fileIndex}.{(_fileFormat == FileFormat.Tiff ? "tif" : "jpg")}");
                var rc = e.DataSource.DGControl.SetupFileXfer.Set(new TWSetupFileXfer
                {
                    FileName = path,
                    Format = _fileFormat,
                    VRefNum = 0
                });
                if (rc != ReturnCode.Success)
                {
                    _logger.LogDebug("NAPS2.TW - FOPA SetupFileXfer failed: {rc}", rc);
                }
            }
            if (_cancelToken.IsCancellationRequested)
            {
                e.CancelAll = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending TWAIN transfer ready event");
        }
    }

    private TwainMemoryBuffer ToMemoryBuffer(byte[] buffer, TWImageMemXfer memInfo)
    {
        return new TwainMemoryBuffer
        {
            Buffer = ByteString.CopyFrom(buffer),
            Columns = (int) memInfo.Columns,
            Rows = (int) memInfo.Rows,
            XOffset = (int) memInfo.XOffset,
            YOffset = (int) memInfo.YOffset,
            BytesPerRow = (int) memInfo.BytesPerRow
        };
    }

    private static TwainImageData ToImageData(TWImageInfo imageInfo)
    {
        var imageData = new TwainImageData
        {
            Width = imageInfo.ImageWidth,
            Height = imageInfo.ImageLength,
            BitsPerPixel = imageInfo.BitsPerPixel,
            SamplesPerPixel = imageInfo.SamplesPerPixel,
            PixelType = (int) imageInfo.PixelType,
            XRes = imageInfo.XResolution,
            YRes = imageInfo.YResolution
        };
        imageData.BitsPerSample.AddRange(imageInfo.BitsPerSample.Select(x => (int) x));
        return imageData;
    }

    private void ConfigureSource(DataSource source)
    {
        // FOPA: the driver's own settings first, so every explicit option below still wins.
        if (_options.TwainOptions.DriverSettings is { Length: > 0 } driverSettings)
        {
            WriteDriverSettings(source, driverSettings);
        }
        if (_options.TwainOptions.CaptureDriverSettings)
        {
            // The dialog shows what the driver has; nothing of ours goes over it.
            return;
        }

        // Transfer Mode
        if (_options.TwainOptions.TransferMode == TwainTransferMode.Default)
        {
            if (source.Manufacturer.Contains("Kyocera", StringComparison.InvariantCultureIgnoreCase) &&
                (source.ProductFamily.Contains("Ecosys", StringComparison.InvariantCultureIgnoreCase) ||
                 source.Name.Contains("Ecosys", StringComparison.InvariantCultureIgnoreCase)))
            {
                _logger.LogDebug("Detected Kyocera Ecosys scanner. Defaulting to Native transfer mode.");
                _options.TwainOptions.TransferMode = TwainTransferMode.Native;
            }
            else
            {
                _logger.LogDebug("Defaulting to Memory transfer mode.");
                _options.TwainOptions.TransferMode = TwainTransferMode.Memory;
            }
        }
        if (_options.TwainOptions.TransferMode == TwainTransferMode.File && _options.UseNativeUI)
        {
            // The driver's dialog decides the pixel type, and with it whether JPEG or G4 applies.
            _logger.LogDebug("NAPS2.TW - FOPA File transfer is not used with the native UI; Native instead.");
            _options.TwainOptions.TransferMode = TwainTransferMode.Native;
        }
        if (_options.TwainOptions.TransferMode == TwainTransferMode.Memory)
        {
            _logger.LogDebug("Transfer mode: Memory");
            source.Capabilities.ICapXferMech.SetValue(XferMech.Memory);
        }
        else if (_options.TwainOptions.TransferMode == TwainTransferMode.File)
        {
            _logger.LogDebug("Transfer mode: File (set after the pixel type)");
        }
        else
        {
            _logger.LogDebug("Transfer mode: Native");
        }

        if (_options.UseNativeUI)
        {
            return;
        }

        // Progress UI
        if (!_options.TwainOptions.ShowProgress)
        {
            source.Capabilities.CapIndicators.SetValue(BoolType.False);
        }

        // FOPA: a driver sajat profilja bekapcsolva tarthatja az ures oldal eldobast
        // (fi-7700-on mérve: ICAP_AUTODISCARDBLANKPAGES = -1 = Auto), amitol a duplex
        // koteg fele annyi kepet ad, es a lapok parositasa elcsuszik.
        if (source.Capabilities.ICapAutoDiscardBlankPages.IsSupported)
        {
            var before = source.Capabilities.ICapAutoDiscardBlankPages.GetCurrent();
            var rc = source.Capabilities.ICapAutoDiscardBlankPages.SetValue(BlankPage.Disable);
            _logger.LogDebug(
                "NAPS2.TW - FOPA ICapAutoDiscardBlankPages: before={before} rc={rc} after={after}",
                before, rc, source.Capabilities.ICapAutoDiscardBlankPages.GetCurrent());
        }
        else
        {
            _logger.LogDebug("NAPS2.TW - FOPA ICapAutoDiscardBlankPages not supported");
        }

        // FOPA: az oldalankenti metaadathoz (TWEI_PAPERCOUNT, TWEI_PAGESIDE) ez kell
        if (source.Capabilities.ICapExtImageInfo.IsSupported)
        {
            var rc = source.Capabilities.ICapExtImageInfo.SetValue(BoolType.True);
            _logger.LogDebug("NAPS2.TW - FOPA ICapExtImageInfo enabled, rc={rc}", rc);
        }
        else
        {
            _logger.LogDebug("NAPS2.TW - FOPA ICapExtImageInfo not supported");
        }

        ConfigureFopaOptions(source);

        // Paper Source
        switch (_options.PaperSource)
        {
            case PaperSource.Auto: // Assume the data source will ignore if unsupported
            case PaperSource.Flatbed:
                source.Capabilities.CapFeederEnabled.SetValue(BoolType.False);
                source.Capabilities.CapDuplexEnabled.SetValue(BoolType.False);
                break;
            case PaperSource.Feeder:
                source.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
                source.Capabilities.CapDuplexEnabled.SetValue(BoolType.False);
                break;
            case PaperSource.Duplex:
                source.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
                source.Capabilities.CapDuplexEnabled.SetValue(BoolType.True);
                break;
        }

        // TODO: Should we add an "Automatic" option in the NAPS2 GUI instead of making "Glass" = Auto?
        // For "Auto", choose the feeder if it has paper, otherwise the flatbed.
        if (_options.PaperSource == PaperSource.Auto)
        {
            if (source.Capabilities.CapAutomaticSenseMedium.IsSupported)
            {
                source.Capabilities.CapAutomaticSenseMedium.SetValue(BoolType.True);
            }
            else if (source.Capabilities.CapFeederLoaded.IsSupported &&
                     source.Capabilities.CapFeederLoaded.GetCurrent() == BoolType.True)
            {
                source.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
            }
        }

        // Bit Depth
        switch (_options.BitDepth)
        {
            case BitDepth.Color:
                source.Capabilities.ICapPixelType.SetValue(PixelType.RGB);
                source.Capabilities.ICapBitDepth.SetValue(24);
                break;
            case BitDepth.Grayscale:
                source.Capabilities.ICapPixelType.SetValue(PixelType.Gray);
                source.Capabilities.ICapBitDepth.SetValue(8);
                break;
            case BitDepth.BlackAndWhite:
                source.Capabilities.ICapPixelType.SetValue(PixelType.BlackWhite);
                source.Capabilities.ICapBitDepth.SetValue(1);
                break;
        }

        // Page Size, Horizontal Align
        float pageWidth = _options.PageSize!.WidthInThousandthsOfAnInch / 1000.0f;
        float pageHeight = _options.PageSize.HeightInThousandthsOfAnInch / 1000.0f;
        var pageMaxWidthFixed = source.Capabilities.ICapPhysicalWidth.GetCurrent();
        float pageMaxWidth = pageMaxWidthFixed.Whole + (pageMaxWidthFixed.Fraction / (float) UInt16.MaxValue);

        float horizontalOffset = 0.0f;
        if (_options.PageAlign == HorizontalAlign.Center)
            horizontalOffset = (pageMaxWidth - pageWidth) / 2;
        else if (_options.PageAlign == HorizontalAlign.Left)
            horizontalOffset = (pageMaxWidth - pageWidth);

        source.Capabilities.ICapUnits.SetValue(Unit.Inches);
        source.DGImage.ImageLayout.Get(out TWImageLayout imageLayout);
        imageLayout.Frame = new TWFrame
        {
            Left = horizontalOffset,
            Right = horizontalOffset + pageWidth,
            Top = 0,
            Bottom = pageHeight
        };
        source.DGImage.ImageLayout.Set(imageLayout);

        // Brightness, Contrast
        // Conveniently, the range of values used in settings (-1000 to +1000) is the same range TWAIN supports
        if (!_options.BrightnessContrastAfterScan)
        {
            source.Capabilities.ICapBrightness.SetValue(_options.Brightness);
            source.Capabilities.ICapContrast.SetValue(_options.Contrast);
        }

        // Resolution
        SetClosest(source.Capabilities.ICapXResolution, _options.Dpi);
        SetClosest(source.Capabilities.ICapYResolution, _options.Dpi);
    }

    private void SetClosest(ICapWrapper<TWFix32> cap, int value)
    {
        if (!cap.CanGet)
        {
            cap.SetValue(value);
            return;
        }
        var possibleValues = cap.GetValues().ToList();
        if (possibleValues.Count == 0)
        {
            cap.SetValue(value);
            return;
        }
        var closest = possibleValues.OrderBy(v => Math.Abs(v - value)).First();
        cap.SetValue(closest);
    }
    /// <summary>FOPA: double feed stop, patch codes and the imprinter, each only when asked for.</summary>
    private void ConfigureFopaOptions(DataSource source)
    {
        var twain = _options.TwainOptions;
        if (twain.StopOnDoubleFeed)
        {
            if (source.Capabilities.CapDoubleFeedDetection.IsSupported)
            {
                var rc = source.Capabilities.CapDoubleFeedDetection.SetValue(DoubleFeedDetection.Ultrasonic);
                _logger.LogDebug("NAPS2.TW - FOPA CapDoubleFeedDetection ultrasonic: rc={rc}", rc);
            }
            if (source.Capabilities.CapDoubleFeedDetectionResponse.IsSupported)
            {
                var rc = source.Capabilities.CapDoubleFeedDetectionResponse.SetValue(DoubleFeedDetectionResponse.Stop);
                _logger.LogDebug("NAPS2.TW - FOPA CapDoubleFeedDetectionResponse stop: rc={rc}", rc);
            }
            else
            {
                _logger.LogDebug("NAPS2.TW - FOPA double feed response not supported");
            }
        }
        if (twain.DetectPatchCodes)
        {
            var rc = source.Capabilities.ICapPatchCodeDetectionEnabled.IsSupported
                ? source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True)
                : ReturnCode.Failure;
            _logger.LogDebug("NAPS2.TW - FOPA ICapPatchCodeDetectionEnabled: rc={rc}", rc);
        }
        if (twain.ImprinterText != null)
        {
            if (source.Capabilities.CapPrinterEnabled.IsSupported)
            {
                source.Capabilities.CapPrinterEnabled.SetValue(BoolType.True);
                source.Capabilities.CapPrinterMode.SetValue(PrinterMode.SingleString);
                var rc = source.Capabilities.CapPrinterString.SetValue(twain.ImprinterText);
                _logger.LogDebug("NAPS2.TW - FOPA imprinter text set: rc={rc}", rc);
            }
            else
            {
                _logger.LogDebug("NAPS2.TW - FOPA no imprinter");
            }
        }
        if (twain.TransferMode == TwainTransferMode.File)
        {
            ConfigureFileTransfer(source);
        }
    }

    /// <summary>
    /// FOPA: the driver compresses and writes the file, so the uncompressed image never enters the
    /// 32-bit worker. Without the format and compression the driver writes an uncompressed TIFF
    /// (scanner-capture measurement: A4 300 dpi black and white, 1.1 MB instead of 23 KB).
    /// </summary>
    private void ConfigureFileTransfer(DataSource source)
    {
        bool bitonal = _options.BitDepth == BitDepth.BlackAndWhite;
        _fileFormat = bitonal ? FileFormat.Tiff : FileFormat.Jfif;
        var rc1 = source.Capabilities.ICapXferMech.SetValue(XferMech.File);
        var rc2 = source.Capabilities.ICapImageFileFormat.SetValue(_fileFormat);
        var rc3 = source.Capabilities.ICapCompression.SetValue(bitonal ? CompressionType.Group4 : CompressionType.Jpeg);
        var rc4 = bitonal ? ReturnCode.Success : source.Capabilities.ICapJpegQuality.SetValue((JpegQuality) _options.TwainOptions.FileJpegQuality);
        _logger.LogDebug("NAPS2.TW - FOPA file transfer {format}: xfer={rc1} format={rc2} compression={rc3} quality={rc4}",
            _fileFormat, rc1, rc2, rc3, rc4);
        if (rc1 != ReturnCode.Success)
        {
            throw new DeviceException($"TWAIN file transfer is not accepted by the driver: {rc1}");
        }
    }

    // FOPA: DAT_CUSTOMDSDATA. NTwain keeps its typed triplet internal, but DGCustom.DsmEntry takes any
    // triplet (the scanner-capture TwainHost, measured on the fi-7700). TW_CUSTOMDSDATA is InfoLength
    // (TW_UINT32) and hData (TW_HANDLE): 8 bytes in the 32-bit worker.
    private const DataArgumentType DatCustomDsData = (DataArgumentType) 0x000C;

    private void WriteDriverSettings(DataSource source, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr) data.Length);
        if (handle == IntPtr.Zero)
        {
            throw new DeviceException("GlobalAlloc failed for the driver settings.");
        }
        var ptr = Marshal.AllocHGlobal(IntPtr.Size * 2);
        try
        {
            var locked = GlobalLock(handle);
            Marshal.Copy(data, 0, locked, data.Length);
            GlobalUnlock(handle);
            Marshal.WriteInt32(ptr, 0, data.Length);
            Marshal.WriteIntPtr(ptr, IntPtr.Size, handle);
            var rc = source.DGCustom.DsmEntry(DataGroups.Control, DatCustomDsData, Message.Set, ptr);
            _logger.LogDebug("NAPS2.TW - FOPA driver settings applied ({bytes} bytes): rc={rc}", data.Length, rc);
            if (rc != ReturnCode.Success)
            {
                throw new DeviceException($"The driver refused the saved settings: {rc}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
            GlobalFree(handle);
        }
    }

    private void ReportDriverSettings(DataSource? source)
    {
        if (source == null)
        {
            return;
        }
        var ptr = Marshal.AllocHGlobal(IntPtr.Size * 2);
        try
        {
            Marshal.WriteInt32(ptr, 0, 0);
            Marshal.WriteIntPtr(ptr, IntPtr.Size, IntPtr.Zero);
            var rc = source.DGCustom.DsmEntry(DataGroups.Control, DatCustomDsData, Message.Get, ptr);
            var length = Marshal.ReadInt32(ptr, 0);
            var handle = Marshal.ReadIntPtr(ptr, IntPtr.Size);
            _logger.LogDebug("NAPS2.TW - FOPA driver settings read: rc={rc} bytes={bytes}", rc, length);
            if (rc != ReturnCode.Success || length <= 0 || handle == IntPtr.Zero)
            {
                return;
            }
            var locked = GlobalLock(handle);
            var buffer = new byte[length];
            Marshal.Copy(locked, buffer, 0, length);
            GlobalUnlock(handle);
            GlobalFree(handle);
            _twainEvents.DriverSettings(new TwainDriverSettings { Data = ByteString.CopyFrom(buffer) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NAPS2.TW - FOPA could not read the driver settings");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private const uint GmemMoveable = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
#endif
