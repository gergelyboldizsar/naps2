using NAPS2.Remoting.Worker;

namespace NAPS2.Scan.Internal.Twain;

internal class TwainEvents : ITwainEvents
{
    private readonly Action<TwainPageStart> _pageStartCallback;
    private readonly Action<TwainPageMetadata> _pageMetadataCallback;
    private readonly Action<TwainNativeImage> _nativeImageCallback;
    private readonly Action<TwainMemoryBuffer> _memoryBufferCallback;
    private readonly Action<TwainTransferCanceled> _transferCanceledCallback;

    public TwainEvents(Action<TwainPageStart> pageStartCallback, Action<TwainNativeImage> nativeImageCallback,
        Action<TwainMemoryBuffer> memoryBufferCallback, Action<TwainTransferCanceled> transferCanceledCallback,
        Action<TwainPageMetadata> pageMetadataCallback)
    {
        _pageStartCallback = pageStartCallback;
        _pageMetadataCallback = pageMetadataCallback;
        _nativeImageCallback = nativeImageCallback;
        _memoryBufferCallback = memoryBufferCallback;
        _transferCanceledCallback = transferCanceledCallback;
    }

    public void PageStart(TwainPageStart pageStart)
    {
        _pageStartCallback(pageStart);
    }

    public void PageMetadata(TwainPageMetadata pageMetadata)
    {
        _pageMetadataCallback(pageMetadata);
    }

    public void NativeImageTransferred(TwainNativeImage nativeImage)
    {
        _nativeImageCallback(nativeImage);
    }

    public void MemoryBufferTransferred(TwainMemoryBuffer memoryBuffer)
    {
        _memoryBufferCallback(memoryBuffer);
    }

    public void TransferCanceled(TwainTransferCanceled transferCanceled)
    {
        _transferCanceledCallback(transferCanceled);
    }
}