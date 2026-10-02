using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using UTStudio.Visualization.Core.Scan2D;

namespace UTStudio.Visualization.Core;

internal enum BScanVisualBufferKind
{
    StoreIndex,
    DetachedSamples,
    DetachedIndex,
    Raster,
    Scratch,
    KernelIndices,
    KernelWeights,
    TraceCopy
}

internal readonly record struct BScanVisualMemorySnapshot(
    long LimitBytes,
    long OwnedBytes,
    long ActiveBytes,
    long RetainedBytes,
    long MaximumOwnedBytes,
    int ActiveOwners,
    int RetainedBuffers);

internal interface IBScanVisualBufferOwner<T> : IMemoryOwner<T> where T : unmanaged
{
    T[] Array { get; }
    int Capacity { get; }
    long CapacityBytes { get; }
}

/// <summary>
/// Owns every managed payload buffer charged to the neutral B-Scan visual budget.
/// The limit covers array payload capacity held actively or retained for reuse; CLR
/// object headers, GC heap accounting and process working set are intentionally outside it.
/// </summary>
internal sealed class BoundedBScanVisualBufferPool : IAsyncDisposable
{
    private const int MaximumBucketKeys = 32;
    private const int MaximumRetainedPerBucket = 1;
    private readonly object _gate = new();
    private readonly long _limitBytes;
    private readonly Dictionary<BufferKey, Stack<Array>> _retained = [];
    private long _ownedBytes;
    private long _activeBytes;
    private long _retainedBytes;
    private long _maximumOwnedBytes;
    private int _activeOwners;
    private int _retainedBuffers;
    private bool _closing;
    private TaskCompletionSource? _drained;

    internal Action<BScanVisualBufferKind>? AfterBackingStorageAcquired { get; set; }

    internal BoundedBScanVisualBufferPool(long limitBytes)
    {
        if (limitBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(limitBytes)); }
        _limitBytes = limitBytes;
    }

    internal BScanVisualMemorySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new(_limitBytes, _ownedBytes, _activeBytes, _retainedBytes,
                    _maximumOwnedBytes, _activeOwners, _retainedBuffers);
            }
        }
    }

    internal IBScanVisualBufferOwner<T> Rent<T>(int minimumLength, BScanVisualBufferKind kind)
        where T : unmanaged
    {
        if (minimumLength <= 0) { throw new ArgumentOutOfRangeException(nameof(minimumLength)); }
        int capacity = GetBucketCapacity(minimumLength);
        int elementBytes = Unsafe.SizeOf<T>();
        long capacityBytes = checked((long)capacity * elementBytes);
        var key = new BufferKey(typeof(T).TypeHandle, kind, capacity);
        T[]? cached = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_retained.TryGetValue(key, out Stack<Array>? bucket) && bucket.Count != 0)
            {
                cached = (T[])bucket.Pop();
                _retainedBytes = checked(_retainedBytes - capacityBytes);
                _retainedBuffers--;
                _activeBytes = checked(_activeBytes + capacityBytes);
                _activeOwners++;
            }
            else
            {
                RemoveEmptyBucketKeysUnderLock();
                EvictRetainedUntilFitsUnderLock(capacityBytes);
                if (_retained.Count >= MaximumBucketKeys && !_retained.ContainsKey(key))
                { EvictOneRetainedBucketUnderLock(); }
                if (_retained.Count >= MaximumBucketKeys && !_retained.ContainsKey(key))
                { throw new InvalidOperationException("The B-Scan visual buffer bucket limit is exhausted."); }
                if (capacityBytes > _limitBytes - _ownedBytes)
                { throw new InvalidOperationException("The B-Scan visual memory budget is exhausted."); }
                if (!_retained.ContainsKey(key)) { _retained.Add(key, new()); }
                _ownedBytes = checked(_ownedBytes + capacityBytes);
                _activeBytes = checked(_activeBytes + capacityBytes);
                _activeOwners++;
                _maximumOwnedBytes = Math.Max(_maximumOwnedBytes, _ownedBytes);
            }
            ValidateUnderLock();
        }

        if (cached is not null)
        {
            try
            {
                AfterBackingStorageAcquired?.Invoke(kind);
                return new Owner<T>(this, key, cached, capacityBytes);
            }
            catch
            {
                Return(key, cached, capacityBytes);
                throw;
            }
        }
        try
        {
            var array = new T[capacity];
            AfterBackingStorageAcquired?.Invoke(kind);
            return new Owner<T>(this, key, array, capacityBytes);
        }
        catch
        {
            RollbackCreation(capacityBytes);
            throw;
        }
    }

    internal void TrimRetained()
    {
        lock (_gate)
        {
            foreach ((BufferKey key, Stack<Array> bucket) in _retained)
            {
                while (bucket.Count != 0)
                {
                    Array array = bucket.Pop();
                    long bytes = checked((long)key.Capacity * GetElementSize(array));
                    _retainedBytes = checked(_retainedBytes - bytes);
                    _ownedBytes = checked(_ownedBytes - bytes);
                    _retainedBuffers--;
                }
            }
            _retained.Clear();
            ValidateUnderLock();
            CompleteDrainUnderLock();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_closing)
            {
                _closing = true;
                foreach ((BufferKey key, Stack<Array> bucket) in _retained)
                {
                    while (bucket.Count != 0)
                    {
                        Array array = bucket.Pop();
                        long bytes = checked((long)key.Capacity * GetElementSize(array));
                        _retainedBytes = checked(_retainedBytes - bytes);
                        _ownedBytes = checked(_ownedBytes - bytes);
                        _retainedBuffers--;
                    }
                }
                _retained.Clear();
                ValidateUnderLock();
            }
            if (_activeOwners == 0) { return ValueTask.CompletedTask; }
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(_drained.Task);
        }
    }

    internal static int GetBucketCapacity(int minimumLength)
    {
        uint value = BitOperations.RoundUpToPowerOf2((uint)minimumLength);
        if (value == 0 || value > int.MaxValue)
        { throw new OverflowException("The requested B-Scan visual buffer capacity is too large."); }
        return (int)value;
    }

    private static int GetElementSize(Array array) => array switch
    {
        byte[] => sizeof(byte),
        short[] => sizeof(short),
        int[] => sizeof(int),
        double[] => sizeof(double),
        BScanIndexedMinMax[] => Unsafe.SizeOf<BScanIndexedMinMax>(),
        _ => throw new InvalidOperationException("Unsupported B-Scan visual buffer type.")
    };

    private void RollbackCreation(long capacityBytes)
    {
        lock (_gate)
        {
            _activeOwners--;
            _activeBytes = checked(_activeBytes - capacityBytes);
            _ownedBytes = checked(_ownedBytes - capacityBytes);
            ValidateUnderLock();
            CompleteDrainUnderLock();
        }
    }

    private void Return<T>(BufferKey key, T[] array, long capacityBytes) where T : unmanaged
    {
        lock (_gate)
        {
            _activeOwners--;
            _activeBytes = checked(_activeBytes - capacityBytes);
            bool retain = !_closing;
            if (retain)
            {
                if (!_retained.TryGetValue(key, out Stack<Array>? bucket))
                {
                    if (_retained.Count >= MaximumBucketKeys) { retain = false; }
                    else
                    {
                        bucket = new();
                        _retained.Add(key, bucket);
                    }
                }
                retain = retain && bucket!.Count < MaximumRetainedPerBucket;
                if (retain)
                {
                    if (key.Kind is BScanVisualBufferKind.DetachedSamples or
                        BScanVisualBufferKind.DetachedIndex or BScanVisualBufferKind.TraceCopy)
                    { Array.Clear(array); }
                    bucket!.Push(array);
                    _retainedBytes = checked(_retainedBytes + capacityBytes);
                    _retainedBuffers++;
                }
            }
            if (!retain) { _ownedBytes = checked(_ownedBytes - capacityBytes); }
            ValidateUnderLock();
            CompleteDrainUnderLock();
        }
    }

    private void CompleteDrainUnderLock()
    {
        if (_closing && _activeOwners == 0)
        {
            if (_ownedBytes != 0 || _activeBytes != 0 || _retainedBytes != 0)
            { throw new InvalidOperationException("The B-Scan visual memory ledger did not drain to zero."); }
            _drained?.TrySetResult();
        }
    }

    private void RemoveEmptyBucketKeysUnderLock()
    {
        foreach (BufferKey key in _retained.Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key).ToArray())
        { _retained.Remove(key); }
    }

    private void EvictRetainedUntilFitsUnderLock(long requestedBytes)
    {
        if (requestedBytes <= _limitBytes - _ownedBytes) { return; }
        foreach ((BufferKey key, Stack<Array> bucket) in _retained.ToArray())
        {
            while (bucket.Count != 0 && requestedBytes > _limitBytes - _ownedBytes)
            {
                Array array = bucket.Pop();
                long bytes = checked((long)key.Capacity * GetElementSize(array));
                _retainedBytes = checked(_retainedBytes - bytes);
                _ownedBytes = checked(_ownedBytes - bytes);
                _retainedBuffers--;
            }
            if (bucket.Count == 0) { _retained.Remove(key); }
            if (requestedBytes <= _limitBytes - _ownedBytes) { break; }
        }
        ValidateUnderLock();
    }

    private void EvictOneRetainedBucketUnderLock()
    {
        foreach ((BufferKey key, Stack<Array> bucket) in _retained)
        {
            if (bucket.Count == 0) { continue; }
            while (bucket.Count != 0)
            {
                Array array = bucket.Pop();
                long bytes = checked((long)key.Capacity * GetElementSize(array));
                _retainedBytes = checked(_retainedBytes - bytes);
                _ownedBytes = checked(_ownedBytes - bytes);
                _retainedBuffers--;
            }
            _retained.Remove(key);
            ValidateUnderLock();
            return;
        }
    }

    private void ValidateUnderLock()
    {
        if (_ownedBytes < 0 || _activeBytes < 0 || _retainedBytes < 0 ||
            _activeOwners < 0 || _retainedBuffers < 0 ||
            _ownedBytes != checked(_activeBytes + _retainedBytes) || _ownedBytes > _limitBytes)
        { throw new InvalidOperationException("The B-Scan visual memory ledger is inconsistent."); }
    }

    private readonly record struct BufferKey(
        RuntimeTypeHandle ElementType,
        BScanVisualBufferKind Kind,
        int Capacity);

    private sealed class Owner<T>(BoundedBScanVisualBufferPool pool, BufferKey key,
        T[] array, long capacityBytes) : IBScanVisualBufferOwner<T> where T : unmanaged
    {
        private T[]? _array = array;
        public T[] Array => _array ?? throw new ObjectDisposedException(nameof(Owner<T>));
        public int Capacity => Array.Length;
        public long CapacityBytes => capacityBytes;
        public Memory<T> Memory => Array;
        public void Dispose()
        {
            T[]? returned = Interlocked.Exchange(ref _array, null);
            if (returned is not null) { pool.Return(key, returned, capacityBytes); }
        }
    }
}

internal sealed class BoundedByteOwnerPool(BoundedBScanVisualBufferPool buffers) : IByteOwnerPool
{
    public int GetMaximumRentalLength(int minimumLength) =>
        BoundedBScanVisualBufferPool.GetBucketCapacity(minimumLength);

    public IMemoryOwner<byte> Rent(int minimumLength) =>
        buffers.Rent<byte>(minimumLength, BScanVisualBufferKind.Raster);

    public IMemoryOwner<byte> Rent(int minimumLength, BScanVisualBufferKind kind) =>
        buffers.Rent<byte>(minimumLength, kind);
}

internal sealed class BoundedTraceCopyOwnerPool(BoundedBScanVisualBufferPool buffers) : ITraceCopyOwnerPool
{
    public IMemoryOwner<short> Rent(int minimumLength) =>
        buffers.Rent<short>(minimumLength, BScanVisualBufferKind.TraceCopy);
}

internal sealed class BoundedLanczosTablePool(BoundedBScanVisualBufferPool buffers) : ILanczosTablePool
{
    private readonly object _gate = new();
    private readonly Dictionary<int[], IBScanVisualBufferOwner<int>> _indices =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<double[], IBScanVisualBufferOwner<double>> _weights =
        new(ReferenceEqualityComparer.Instance);

    public int GetMaximumIndicesLength(int minimumLength) =>
        BoundedBScanVisualBufferPool.GetBucketCapacity(minimumLength);
    public int GetMaximumWeightsLength(int minimumLength) =>
        BoundedBScanVisualBufferPool.GetBucketCapacity(minimumLength);

    public int[] RentIndices(int minimumLength)
    {
        IBScanVisualBufferOwner<int> owner = buffers.Rent<int>(minimumLength,
            BScanVisualBufferKind.KernelIndices);
        try
        {
            lock (_gate) { _indices.Add(owner.Array, owner); }
            return owner.Array;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public double[] RentWeights(int minimumLength)
    {
        IBScanVisualBufferOwner<double> owner = buffers.Rent<double>(minimumLength,
            BScanVisualBufferKind.KernelWeights);
        try
        {
            lock (_gate) { _weights.Add(owner.Array, owner); }
            return owner.Array;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public void Return(int[] array)
    {
        IBScanVisualBufferOwner<int>? owner;
        lock (_gate)
        {
            if (!_indices.Remove(array, out owner))
            { throw new InvalidOperationException("The indices buffer is not owned by this pool."); }
        }
        owner.Dispose();
    }

    public void Return(double[] array)
    {
        IBScanVisualBufferOwner<double>? owner;
        lock (_gate)
        {
            if (!_weights.Remove(array, out owner))
            { throw new InvalidOperationException("The weights buffer is not owned by this pool."); }
        }
        owner.Dispose();
    }
}
