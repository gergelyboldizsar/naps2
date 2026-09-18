using NAPS2.Remoting.Worker;

namespace NAPS2.Scan.Internal.Twain;

internal interface ITwainEvents
{
    void PageStart(TwainPageStart pageStart);

    /// <summary>
    /// FOPA: per page metadata (physical sheet number, real page side) read from the driver
    /// after the transfer. Raised before the image events of the same page.
    /// </summary>
    void PageMetadata(TwainPageMetadata pageMetadata);

    void NativeImageTransferred(TwainNativeImage nativeImage);

    void MemoryBufferTransferred(TwainMemoryBuffer memoryBuffer);

    void TransferCanceled(TwainTransferCanceled transferCanceled);
}