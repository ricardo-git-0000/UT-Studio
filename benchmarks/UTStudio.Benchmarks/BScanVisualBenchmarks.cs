using BenchmarkDotNet.Attributes;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Benchmarks;

/// <summary>Diagnostic benchmarks; results are not throughput guarantees.</summary>
[MemoryDiagnoser]
public class BScanFullRasterBenchmarks
{
    [ParamsSource(nameof(RasterSizes))] public int RasterSize { get; set; }
    [ParamsSource(nameof(Modes))]
    public Scan2DInterpolationMode Mode { get; set; }
    private BScanVisualBenchmarkFixture _fixture = null!;

    public IEnumerable<int> RasterSizes => DiagnosticParameter("UTSTUDIO_SCAN2D_SIZE", [256, 1024], int.Parse);
    public IEnumerable<Scan2DInterpolationMode> Modes => DiagnosticParameter("UTSTUDIO_SCAN2D_MODE",
        [Scan2DInterpolationMode.None, Scan2DInterpolationMode.NearestNeighbor,
            Scan2DInterpolationMode.Bilinear, Scan2DInterpolationMode.Bicubic],
        value => Enum.Parse<Scan2DInterpolationMode>(value, ignoreCase: true));

    [GlobalSetup]
    public void Setup()
    {
        Console.WriteLine($"SCAN2D_SETUP_START {DateTimeOffset.UtcNow:O} mode={Mode} size={RasterSize}");
        _fixture = BScanVisualBenchmarkFixture.Create(128, 2048, RasterSize, mode: Mode);
        Console.WriteLine($"SCAN2D_SETUP_END {DateTimeOffset.UtcNow:O} {_fixture.DiagnosticState}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Console.WriteLine($"SCAN2D_CLEANUP_START {DateTimeOffset.UtcNow:O} {_fixture.DiagnosticState}");
        _fixture.Dispose();
        Console.WriteLine($"SCAN2D_CLEANUP_END {DateTimeOffset.UtcNow:O} {_fixture.DiagnosticState}");
    }

    [Benchmark(Description = "B-Scan full neutral raster render and owner release")]
    public int FullRaster() => _fixture.RenderFull();

    private static IEnumerable<T> DiagnosticParameter<T>(string name, T[] defaults, Func<string, T> parse)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? defaults
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(parse);
    }
}

[MemoryDiagnoser]
public class BScanBipolarMappingBenchmarks
{
    private readonly short[] _samples = BenchmarkData.Samples(65_535);

    [Benchmark(OperationsPerInvoke = 65_535, Description = "B-Scan bipolar Int16 to neutral byte")]
    public byte Map()
    {
        byte result = 0;
        foreach (short sample in _samples) { result ^= BScanBipolarIntensity8.Map(sample); }
        return result;
    }
}

[MemoryDiagnoser]
public class BScanIncrementalRasterBenchmarks
{
    [Params(1, 8)] public int DirtyColumns { get; set; }
    private BScanVisualBenchmarkFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = BScanVisualBenchmarkFixture.CreateIncremental(DirtyColumns, wrap: false);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(Description = "B-Scan incremental contiguous columns")]
    public int Incremental() => _fixture.RenderPrepared();
}

[MemoryDiagnoser]
public class BScanWrapIncrementalBenchmarks
{
    private BScanVisualBenchmarkFixture _fixture = null!;
    [IterationSetup] public void Setup() => _fixture = BScanVisualBenchmarkFixture.CreateIncremental(1, wrap: true);
    [IterationCleanup] public void Cleanup() => _fixture.Dispose();
    [Benchmark(Description = "B-Scan incremental request after ring wrap")]
    public int IncrementalAfterWrap() => _fixture.RenderPrepared();
}

[MemoryDiagnoser]
public class BScanReductionBenchmarks
{
    private BScanVisualBenchmarkFixture _temporal = null!;
    private BScanVisualBenchmarkFixture _spatial = null!;
    [IterationSetup]
    public void Setup()
    {
        _temporal = BScanVisualBenchmarkFixture.Create(1, 65_535, 256);
        _spatial = BScanVisualBenchmarkFixture.Create(256, 2048, 64);
    }
    [IterationCleanup] public void Cleanup() { _temporal.Dispose(); _spatial.Dispose(); }
    [Benchmark(Description = "B-Scan temporal min/max reduction")]
    public int TemporalReduction() => _temporal.RenderFull();
    [Benchmark(Description = "B-Scan spatial multi-column reduction")]
    public int SpatialReduction() => _spatial.RenderFull();
}

[MemoryDiagnoser]
public class BScanPublicationAdmissionBenchmarks
{
    private BScanVisualBenchmarkFixture _withoutObserver = null!;
    private BScanVisualBenchmarkFixture _withObserver = null!;
    [IterationSetup]
    public void Setup()
    {
        _withoutObserver = BScanVisualBenchmarkFixture.Create(16, 2048, 256);
        _withObserver = BScanVisualBenchmarkFixture.Create(16, 2048, 256, subscribe: true);
    }
    [IterationCleanup] public void Cleanup() { _withoutObserver.Dispose(); _withObserver.Dispose(); }
    [Benchmark(Description = "B-Scan coalesced publication admission without observer")]
    public void CoalesceWithoutObserver() => _withoutObserver.RequestBurst();
    [Benchmark(Description = "B-Scan coalesced publication admission with healthy observer")]
    public void CoalesceWithObserver() => _withObserver.RequestBurst();
    [Benchmark(Description = "B-Scan superseded candidate admission")]
    public void SupersedeCandidate() => _withObserver.RequestSupersession();
}

internal sealed class BScanVisualBenchmarkFixture : IDisposable
{
    private readonly BScanIngress _ingress;
    private readonly BScanVisualDelivery _delivery;
    private readonly BScanRenderRequest _request;
    private BScanRenderRequest _preparedRequest;
    private IDisposable? _subscription;
    private ulong _sequence;

    internal string DiagnosticState
    {
        get
        {
            BScanVisualDiagnostics visual = _delivery.Diagnostics;
            BScanDiagnostics ingress = _ingress.Diagnostics;
            return $"owners={visual.ActiveRasterOwners} ownerBytes={visual.VisualBytesRented} " +
                $"activeBuilds={visual.ActiveBuilds} leases={ingress.ActiveReadLeases} " +
                $"blocks={ingress.BlocksRented - ingress.BlocksReturned}";
        }
    }

    private BScanVisualBenchmarkFixture(BScanIngress ingress, BScanVisualDelivery delivery,
        BScanRenderRequest request)
    { _ingress = ingress; _delivery = delivery; _request = request; _preparedRequest = request; }

    internal static BScanVisualBenchmarkFixture Create(int columns, int samples, int rasterSize,
        bool subscribe = false, int? initialColumns = null,
        Scan2DInterpolationMode mode = Scan2DInterpolationMode.None)
    {
        int initial = initialColumns ?? columns;
        var options = new BScanOptions(32L * 1024 * 1024, 16L * 1024 * 1024,
            columns, columns, 64);
        var ingress = new BScanIngress(options);
        var metadata = BenchmarkData.Metadata(samples);
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = initial;
        ingress.AfterIndexCommit = _ => { if (Interlocked.Decrement(ref remaining) == 0) { indexed.TrySetResult(); } };
        ingress.OpenRun(metadata);
        short[] data = BenchmarkData.Samples(samples);
        for (int column = 0; column < initial; column++)
        {
            ingress.Accept(metadata, (ulong)column, (ulong)column, TimeSpan.FromTicks(column), null, data);
        }
        indexed.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        var delivery = new BScanVisualDelivery(ingress);
        var range = new BScanAcquisitionRange(0, (ulong)columns - 1);
        var time = ScanTimeViewportOperations.Create(metadata.Configuration.FirstSampleOffsetSeconds,
            metadata.Configuration.FirstSampleOffsetSeconds + samples / metadata.Configuration.SampleRateHz);
        var request = new BScanRenderRequest(metadata.RunId, BScanSpatialViewportOperations.Reset(range),
            time, rasterSize, rasterSize, 1, Resampling: new(mode));
        var fixture = new BScanVisualBenchmarkFixture(ingress, delivery, request) { _sequence = (ulong)initial };
        if (subscribe) { fixture._subscription = delivery.Subscribe(new Observer()); }
        return fixture;
    }

    internal static BScanVisualBenchmarkFixture CreateIncremental(int dirtyColumns, bool wrap)
    {
        const int capacity = 16;
        BScanVisualBenchmarkFixture fixture = Create(capacity, 2048, 256,
            initialColumns: wrap ? capacity : 4);
        using BScanRasterRegion baseline = fixture.GetRegion(fixture._request);
        BScanVisualSnapshot snapshot = baseline.Snapshot;
        int additions = wrap ? 1 : dirtyColumns;
        fixture.AddColumns(additions);
        fixture._preparedRequest = fixture._request with
        {
            DisplayedSnapshotVersion = snapshot.SnapshotVersion,
            DisplayedStoreRevision = snapshot.StoreRevision,
            DisplayedWrapGeneration = wrap ? snapshot.WrapGeneration + 1 : snapshot.WrapGeneration
        };
        return fixture;
    }

    internal int RenderFull()
    {
        using BScanRasterRegion measuredRegion = GetRegion(_request);
        return measuredRegion.Pixels.Length;
    }

    internal int RenderPrepared()
    {
        using BScanRasterRegion measuredRegion = GetRegion(_preparedRequest);
        return measuredRegion.Pixels.Length;
    }

    internal void RequestBurst()
    {
        _delivery.RequestRender(_request);
        _delivery.RequestRender(_request with { PixelWidth = 255 });
        _delivery.RequestRender(_request);
    }

    internal void RequestSupersession()
    {
        _delivery.RequestRender(_request with { PixelWidth = 255 });
        _delivery.RequestRender(_request with { PixelWidth = 256 });
    }

    private BScanRasterRegion GetRegion(BScanRenderRequest request)
    {
        BScanRasterResult result = _delivery.GetRegionAsync(request).AsTask().GetAwaiter().GetResult();
        if (result.Kind != BScanRasterResultKind.Available)
        { throw new InvalidOperationException(result.Error ?? result.Kind.ToString()); }
        return result.Region!;
    }

    private void AddColumns(int count)
    {
        short[] data = BenchmarkData.Samples(2048);
        for (int index = 0; index < count; index++)
        {
            var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ingress.AfterIndexCommit = _ => indexed.TrySetResult();
            ulong sequence = _sequence++;
            _ingress.Accept(BenchmarkData.Metadata(2048), sequence, sequence, TimeSpan.FromTicks((long)sequence), null, data);
            indexed.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        List<Exception> failures = [];
        try { _subscription?.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { _delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        catch (Exception error) { failures.Add(error); }
        try { _ingress.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) { throw new AggregateException("B-Scan benchmark fixture cleanup failed.", failures); }
    }

    private sealed class Observer : IObserver<BScanVisualUpdate>
    {
        public void OnNext(BScanVisualUpdate value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
