using System.Buffers;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

public readonly record struct BScanAcquisitionRange
{
    public BScanAcquisitionRange(ulong first, ulong last)
    {
        if (last < first) { throw new ArgumentOutOfRangeException(nameof(last)); }
        First = first;
        Last = last;
    }

    public ulong First { get; }
    public ulong Last { get; }
}

public readonly record struct BScanSpatialViewport
{
    public BScanSpatialViewport(ulong originAcquisitionIndex, decimal minimumOffset, decimal maximumOffset)
    {
        if (maximumOffset <= minimumOffset) { throw new ArgumentOutOfRangeException(nameof(maximumOffset)); }
        OriginAcquisitionIndex = originAcquisitionIndex;
        MinimumOffset = minimumOffset;
        MaximumOffset = maximumOffset;
    }

    public ulong OriginAcquisitionIndex { get; }
    public decimal MinimumOffset { get; }
    public decimal MaximumOffset { get; }
    public decimal Span => MaximumOffset - MinimumOffset;
}

public static class BScanSpatialViewportOperations
{
    public const decimal MaximumZoom = 4096m;

    public static BScanSpatialViewport Reset(BScanAcquisitionRange domain) =>
        new(domain.First, 0, checked((decimal)(domain.Last - domain.First) + 1));

    public static BScanSpatialViewport ZoomAt(BScanSpatialViewport viewport, BScanAcquisitionRange domain,
        decimal anchorRatio, decimal factor)
    {
        if (anchorRatio is < 0 or > 1) { throw new ArgumentOutOfRangeException(nameof(anchorRatio)); }
        if (factor <= 0) { throw new ArgumentOutOfRangeException(nameof(factor)); }
        BScanSpatialViewport reconciled = Reconcile(viewport, domain);
        decimal domainSpan = checked((decimal)(domain.Last - domain.First) + 1);
        decimal minimumSpan = Math.Max(1m, domainSpan / MaximumZoom);
        decimal span = Math.Clamp(reconciled.Span / factor, minimumSpan, domainSpan);
        decimal anchor = reconciled.MinimumOffset + reconciled.Span * anchorRatio;
        return Clamp(new(reconciled.OriginAcquisitionIndex,
            anchor - span * anchorRatio, anchor + span * (1 - anchorRatio)), domain);
    }

    public static BScanSpatialViewport Pan(BScanSpatialViewport viewport, BScanAcquisitionRange domain, decimal delta)
    {
        decimal minimum;
        try { minimum = checked(viewport.MinimumOffset + delta); }
        catch (OverflowException) { minimum = delta < 0 ? decimal.MinValue : decimal.MaxValue - viewport.Span; }
        decimal maximum;
        try { maximum = checked(minimum + viewport.Span); }
        catch (OverflowException) { maximum = decimal.MaxValue; minimum = maximum - viewport.Span; }
        return Clamp(new(viewport.OriginAcquisitionIndex, minimum, maximum), domain);
    }

    public static BScanSpatialViewport Reconcile(BScanSpatialViewport viewport, BScanAcquisitionRange domain) =>
        Clamp(viewport, domain);

    public static decimal RelativeOffset(ulong acquisitionIndex, ulong origin) => acquisitionIndex >= origin
        ? acquisitionIndex - origin : -(decimal)(origin - acquisitionIndex);

    private static BScanSpatialViewport Clamp(BScanSpatialViewport viewport, BScanAcquisitionRange domain)
    {
        decimal domainMinimum = RelativeOffset(domain.First, viewport.OriginAcquisitionIndex);
        decimal domainMaximum = checked(RelativeOffset(domain.Last, viewport.OriginAcquisitionIndex) + 1);
        decimal domainSpan = domainMaximum - domainMinimum;
        decimal span = Math.Clamp(viewport.Span, Math.Max(1m, domainSpan / MaximumZoom), domainSpan);
        decimal minimum = viewport.MinimumOffset;
        if (minimum < domainMinimum) { minimum = domainMinimum; }
        if (minimum + span > domainMaximum) { minimum = domainMaximum - span; }
        return new(viewport.OriginAcquisitionIndex, minimum, minimum + span);
    }
}

public enum BScanRasterFormat { BipolarIntensity8 = 1 }

/// <summary>Canonical bipolar mapping: 0=no data, 1=-32768, 128=zero, 255=32767.</summary>
public static class BScanBipolarIntensity8
{
    public const byte NoData = 0;
    public const byte Zero = 128;

    public static byte Map(short sample)
    {
        if (sample == 0) { return Zero; }
        if (sample < 0)
        {
            int magnitude = -(int)sample;
            return (byte)(127 - (magnitude - 1) * 126 / 32767);
        }
        return (byte)(129 + ((int)sample - 1) * 126 / 32766);
    }
}

public readonly record struct BScanVisualRevision(ulong SnapshotVersion, ulong StoreRevision, ulong WrapGeneration);
public readonly record struct BScanRasterRectangle(int X, int Y, int Width, int Height);
public readonly record struct BScanStreamIdentity(UtSourceId SourceId, AcquisitionRunId RunId,
    PhysicalChannelId PhysicalChannelId);

[Flags]
public enum BScanFullRefreshReason
{
    None = 0,
    Initial = 1,
    RunChanged = 2,
    BaseRevisionMismatch = 4,
    SnapshotVersionGap = 8,
    WrapChanged = 16,
    DirtyRangeEvicted = 32,
    ViewportChanged = 64,
    ResolutionChanged = 128,
    RenderConfigurationChanged = 256,
    Coalesced = 512,
    Recovery = 1024
}

public readonly record struct BScanDirtyRange(ulong FirstAcquisitionIndex, ulong LastAcquisitionIndex,
    ulong BaseStoreRevision, ulong StoreRevision);

public sealed record BScanRenderRequest(
    AcquisitionRunId RunId,
    BScanSpatialViewport SpatialViewport,
    ScanTimeViewport TimeViewport,
    int PixelWidth,
    int PixelHeight,
    ulong RenderConfigurationVersion,
    ulong? DisplayedSnapshotVersion = null,
    ulong? DisplayedStoreRevision = null,
    ulong? DisplayedWrapGeneration = null,
    string ConsumerId = "default",
    BScanDisplayedVisualIdentity? Displayed = null,
    bool FollowLiveWindow = false)
{
    public BScanRenderRequest Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerId);
        if (!RunId.IsValid) { throw new ArgumentException("A valid run is required.", nameof(RunId)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(PixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PixelWidth, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(PixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PixelHeight, 1024);
        ArgumentNullException.ThrowIfNull(TimeViewport);
        int identityParts = (DisplayedSnapshotVersion.HasValue ? 1 : 0) +
            (DisplayedStoreRevision.HasValue ? 1 : 0) + (DisplayedWrapGeneration.HasValue ? 1 : 0);
        if (identityParts is not 0 and not 3)
        {
            throw new ArgumentException("The displayed visual revision must be supplied as a complete identity.");
        }
        return this;
    }
}

public readonly record struct BScanRenderSignature(BScanSpatialViewport SpatialViewport,
    ScanTimeViewport TimeViewport, int PixelWidth, int PixelHeight, ulong RenderConfigurationVersion);

public sealed record BScanDisplayedVisualIdentity(BScanStreamIdentity Stream, BScanVisualRevision Revision,
    BScanRenderSignature Render);

public sealed record BScanVisualSnapshot(
    AcquisitionRunId RunId,
    ulong SnapshotVersion,
    ulong BaseStoreRevision,
    ulong StoreRevision,
    ulong WrapGeneration,
    BScanAcquisitionRange RepresentedRange,
    BScanAcquisitionRange ResidentRange,
    ScanTimeViewport TemporalDomain,
    BScanSpatialViewport SpatialViewport,
    ScanTimeViewport TimeViewport,
    int RasterWidth,
    int RasterHeight,
    ulong RenderConfigurationVersion,
    BScanDirtyRange DirtyRange,
    BScanRasterRectangle AffectedRegion,
    bool HasDiscontinuities,
    bool RequiresFullRefresh,
    BScanFullRefreshReason FullRefreshReason)
{
    public string ConsumerId { get; init; } = "default";
    public BScanStreamIdentity Stream { get; init; }
    public int HorizontalPixelShift { get; init; }
}

public sealed record BScanVisualUpdate(BScanVisualSnapshot Snapshot);

public enum BScanRasterResultKind { Available, NotAvailable, Stale, ResourceLimit, Cancelled, Faulted }

public interface IBScanRasterRegionProvider
{
    ValueTask<BScanRasterResult> GetRegionAsync(BScanRenderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Neutral metadata and raster source used by Presentation.</summary>
public interface IBScanVisualSource : IObservable<BScanVisualUpdate>, IBScanRasterRegionProvider
{
    BScanVisualDiagnostics Diagnostics { get; }
    bool RegisterConsumer(string consumerId) => true;
    void RequestBootstrap(string consumerId) { }
    void UnregisterConsumer(string consumerId) { }
    void RequestRender(BScanRenderRequest request);
    void AcknowledgeDisplayed(string consumerId, BScanDisplayedVisualIdentity displayed) { }
}

public sealed class BScanRasterResult
{
    private BScanRasterResult(BScanRasterResultKind kind, BScanRasterRegion? region, string? error)
    { Kind = kind; Region = region; Error = error; }
    public BScanRasterResultKind Kind { get; }
    public BScanRasterRegion? Region { get; }
    public string? Error { get; }
    internal static BScanRasterResult Available(BScanRasterRegion region) => new(BScanRasterResultKind.Available, region, null);
    internal static BScanRasterResult NotAvailable() => new(BScanRasterResultKind.NotAvailable, null, null);
    internal static BScanRasterResult Stale() => new(BScanRasterResultKind.Stale, null, null);
    internal static BScanRasterResult ResourceLimit(string error) => new(BScanRasterResultKind.ResourceLimit, null, error);
    internal static BScanRasterResult Cancelled() => new(BScanRasterResultKind.Cancelled, null, null);
    internal static BScanRasterResult Faulted(Exception error) => new(BScanRasterResultKind.Faulted, null,
        string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);
}

public sealed class BScanRasterRegion : IDisposable
{
    private Ownership? _ownership;

    internal BScanRasterRegion(BScanVisualSnapshot snapshot, int x, int y, int width, int height,
        IMemoryOwner<byte> owner, Action release)
    {
        Snapshot = snapshot;
        X = x; Y = y; Width = width; Height = height; Stride = width;
        Format = BScanRasterFormat.BipolarIntensity8;
        if (owner.Memory.Length < checked(width * height))
        {
            owner.Dispose();
            release();
            throw new ArgumentException("The raster owner is too small.", nameof(owner));
        }
        _ownership = new(owner, owner.Memory[..checked(width * height)], release);
    }

    public BScanVisualSnapshot Snapshot { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public BScanRasterFormat Format { get; }
    public ReadOnlyMemory<byte> Pixels => Volatile.Read(ref _ownership)?.Pixels
        ?? throw new ObjectDisposedException(nameof(BScanRasterRegion));

    public void Dispose()
    {
        Ownership? ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null) { return; }
        try { ownership.Owner.Dispose(); }
        finally { ownership.Release(); }
    }

    private sealed record Ownership(IMemoryOwner<byte> Owner, ReadOnlyMemory<byte> Pixels, Action Release);
}
