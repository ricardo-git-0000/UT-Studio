using UTStudio.Contracts.Processing;

namespace UTStudio.Processing.Core;

internal interface IGateScratchPool
{
    bool TryRent(int sampleCount, out GateScratchLease? lease);
    void Return(GateScratchLease lease);
    GateScratchDiagnostics Snapshot { get; }
}

public sealed class GateScratchPool : IGateScratchPool
{
    public const int MaximumSampleCount = 65_535;
    private readonly object _sync = new();
    private readonly int _maximumConcurrentRentals;
    private readonly long _maximumRetainedElements;
    private readonly Dictionary<int, Stack<GateScratchBuffer>> _available = [];
    private readonly HashSet<long> _activeLeases = [];
    private long _nextLeaseId;
    private int _active;
    private int _peak;
    private long _rents;
    private long _returns;
    private long _rejections;
    private long _rentedElements;
    private long _returnedElements;
    private long _retainedCapacity;

    public GateScratchPool(int maximumConcurrentRentals = 4, long maximumRetainedElements = 2_000_000)
    {
        if (maximumConcurrentRentals < 1) { throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRentals)); }
        if (maximumRetainedElements < maximumConcurrentRentals * GateScratchBuffer.ElementCapacityFor(MaximumSampleCount))
        { throw new ArgumentOutOfRangeException(nameof(maximumRetainedElements)); }
        _maximumConcurrentRentals = maximumConcurrentRentals;
        _maximumRetainedElements = maximumRetainedElements;
    }

    bool IGateScratchPool.TryRent(int sampleCount, out GateScratchLease? lease) => TryRent(sampleCount, out lease);
    internal bool TryRent(int sampleCount, out GateScratchLease? lease)
    {
        lease = null;
        if (sampleCount is < 1 or > MaximumSampleCount) { lock (_sync) { _rejections++; } return false; }
        int bucket = Bucket(sampleCount);
        lock (_sync)
        {
            if (_active >= _maximumConcurrentRentals) { _rejections++; return false; }
            GateScratchBuffer buffer;
            if (_available.TryGetValue(bucket, out Stack<GateScratchBuffer>? buffers) && buffers.Count > 0)
            { buffer = buffers.Pop(); }
            else
            {
                long capacity = GateScratchBuffer.ElementCapacityFor(bucket);
                EvictAvailableUntilFits(capacity);
                if (_retainedCapacity + capacity > _maximumRetainedElements) { _rejections++; return false; }
                buffer = new GateScratchBuffer(bucket);
                _retainedCapacity += capacity;
            }
            long id = ++_nextLeaseId;
            _activeLeases.Add(id);
            _active++;
            _peak = Math.Max(_peak, _active);
            _rents++;
            _rentedElements += buffer.ElementCapacity;
            lease = new GateScratchLease(this, id, buffer);
            return true;
        }
    }

    void IGateScratchPool.Return(GateScratchLease lease) => Return(lease);
    internal void Return(GateScratchLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (!ReferenceEquals(lease.Owner, this) || lease.IsReturned || !_activeLeases.Remove(lease.Id))
            { throw new InvalidOperationException("The scratch lease is foreign or has already been returned."); }
            lease.MarkReturned();
            _active--;
            _returns++;
            _returnedElements += lease.Buffer.ElementCapacity;
            if (!_available.TryGetValue(lease.Buffer.SampleCapacity, out Stack<GateScratchBuffer>? buffers))
            { buffers = new(); _available.Add(lease.Buffer.SampleCapacity, buffers); }
            buffers.Push(lease.Buffer);
        }
    }

    public GateScratchDiagnostics Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new(_rents, _returns, _rejections, _active, _peak, _rentedElements,
                    _returnedElements, _retainedCapacity);
            }
        }
    }

    private static int Bucket(int sampleCount)
    {
        int bucket = 16;
        while (bucket < sampleCount && bucket < 65_536) { bucket <<= 1; }
        return bucket;
    }

    private void EvictAvailableUntilFits(long requiredCapacity)
    {
        while (_retainedCapacity + requiredCapacity > _maximumRetainedElements)
        {
            int bucket = _available.Where(x => x.Value.Count > 0).Select(x => x.Key).DefaultIfEmpty().Max();
            if (bucket == 0) { return; }
            GateScratchBuffer removed = _available[bucket].Pop();
            _retainedCapacity -= removed.ElementCapacity;
            if (_available[bucket].Count == 0) { _available.Remove(bucket); }
        }
    }
}

internal sealed class GateScratchLease
{
    internal GateScratchLease(GateScratchPool owner, long id, GateScratchBuffer buffer)
    { Owner = owner; Id = id; Buffer = buffer; }
    internal GateScratchPool Owner { get; }
    internal long Id { get; }
    internal GateScratchBuffer Buffer { get; }
    internal bool IsReturned { get; private set; }
    internal void MarkReturned() => IsReturned = true;
}

internal sealed class GateScratchBuffer
{
    internal GateScratchBuffer(int sampleCapacity)
    {
        SampleCapacity = sampleCapacity;
        Values = new double[sampleCapacity];
        Tree = new double[2 * sampleCapacity];
        Fenwick = new int[sampleCapacity + 1];
        Candidates = new GateCandidate[(sampleCapacity + 1) / 2];
    }
    internal int SampleCapacity { get; }
    internal double[] Values { get; }
    internal double[] Tree { get; }
    internal int[] Fenwick { get; }
    internal GateCandidate[] Candidates { get; }
    internal int ElementCapacity => Values.Length + Tree.Length + Fenwick.Length + Candidates.Length;
    internal static long ElementCapacityFor(int sampleCapacity) =>
        (long)sampleCapacity + (2L * sampleCapacity) + (sampleCapacity + 1L) + ((sampleCapacity + 1L) / 2L);
}

internal struct GateCandidate
{
    internal int Start;
    internal int End;
    internal int Index;
    internal int LeftGreater;
    internal int RightGreater;
    internal double Amplitude;
    internal double Prominence;
    internal bool Suppressed;
}
