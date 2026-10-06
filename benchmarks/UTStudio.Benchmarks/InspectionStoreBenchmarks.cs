using BenchmarkDotNet.Attributes;
using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;
using UTStudio.Inspection.Core;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class InspectionStoreBenchmarks
{
    private static readonly InspectionId Inspection = new(Guid.Parse("70000000-0000-0000-0000-000000000007"));
    private static readonly AcquisitionRunId Run = new(Guid.Parse("71000000-0000-0000-0000-000000000071"));
    private InMemoryInspectionDataStore _store = null!;
    private InMemoryInspectionDataStore _sealStore = null!;
    private short[] _samples = null!;
    private ulong _index;

    [Params(2_048, 65_535)] public int SampleCount { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _store = new(Inspection, Run, new(256L * 1024 * 1024, 10_000, 4 * 1024 * 1024));
        _sealStore = new(Inspection, Run, new(256L * 1024 * 1024, 10_000, checked(SampleCount * sizeof(short))));
        _samples = new short[SampleCount];
        _index = 1;
        _ = _store.AppendAsync(Input(0)).AsTask().GetAwaiter().GetResult();
        _ = _sealStore.AppendAsync(Input(0)).AsTask().GetAwaiter().GetResult();
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _sealStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public InspectionAppendResult Append() => _store.AppendAsync(Input(_index++)).AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public InspectionAppendResult Duplicate()
        => _store.AppendAsync(Input(0)).AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public InspectionAppendResult Conflict()
    {
        short original = _samples[0];
        _samples[0] = 1;
        InspectionAppendResult result = _store.AppendAsync(Input(0)).AsTask().GetAwaiter().GetResult();
        _samples[0] = original;
        return result;
    }

    [Benchmark]
    public InspectionReadStatus ExactRead()
    {
        InspectionTraceInput input = Input(0);
        InspectionTraceReadResult result = _store.ReadAsync(input.Descriptor.Identity).AsTask().GetAwaiter().GetResult();
        result.Lease?.Dispose();
        return result.Status;
    }

    [Benchmark]
    public int BoundedRangeRead()
    {
        InspectionTraceInput input = Input(0);
        InspectionRangeReadResult result = _store.ReadRangeAsync(new(input.Descriptor.Identity.Stream, 0, 16)).AsTask().GetAwaiter().GetResult();
        foreach (InspectionRangeItem item in result.Items) { item.Lease?.Dispose(); }
        return result.Items.Count;
    }

    [Benchmark]
    public InspectionAppendResult CommitThatSealsChunk()
    {
        ulong index = _index++;
        return _sealStore.AppendAsync(Input(index)).AsTask().GetAwaiter().GetResult();
    }

    private InspectionTraceInput Input(ulong index)
    {
        var stream = new UtStreamIdentity(Inspection, Run, new("benchmark"), new(0));
        var descriptor = new TraceDescriptor(new(stream, index), index, TimeSpan.FromTicks((long)index),
            DateTimeOffset.UnixEpoch, null, SampleCount, 50_000_000, 0, UtSignalMode.Rf);
        return new(descriptor, _samples);
    }
}
