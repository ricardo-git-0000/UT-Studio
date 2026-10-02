using System.Buffers;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Visualization.Core;

internal interface IShortArrayPool
{
    short[] Rent(int minimumLength);
    void Return(short[] array);
}

internal sealed class SharedShortArrayPool : IShortArrayPool
{
    internal static SharedShortArrayPool Instance { get; } = new();
    public short[] Rent(int minimumLength) => ArrayPool<short>.Shared.Rent(minimumLength);
    public void Return(short[] array) => ArrayPool<short>.Shared.Return(array);
}

internal interface ITraceCopyOwnerPool
{
    IMemoryOwner<short> Rent(int minimumLength);
}

internal sealed class SharedTraceCopyOwnerPool : ITraceCopyOwnerPool
{
    internal static SharedTraceCopyOwnerPool Instance { get; } = new();
    public IMemoryOwner<short> Rent(int minimumLength) => MemoryPool<short>.Shared.Rent(minimumLength);
}

internal sealed class BScanTraceStore
{
    private const int VisualWriteHeadroom = 4;
    private readonly object _gate = new();
    private readonly IShortArrayPool _pool;
    private readonly BoundedBScanVisualBufferPool _visualBuffers;
    private readonly bool _ownsVisualBuffers;
    private readonly int _sampleCount;
    private readonly int _bucketCount;
    private readonly int _bucketSamples;
    private readonly Block[] _blocks;
    private readonly Slot[] _slots;
    private readonly List<IBScanVisualBufferOwner<BScanIndexedMinMax>> _slotIndexOwners = [];
    private readonly HashSet<BScanTraceIdentity> _evicted = [];
    private readonly Queue<BScanTraceIdentity> _evictionOrder = [];
    private TaskCompletionSource? _leaseDrain;
    private TaskCompletionSource? _writerDrain;
    private Task? _closeTask;
    private bool _accepting = true;
    private bool _leasesAllowed = true;
    private bool _returned;
    private int _nextSlot;
    private ulong _revision;
    private ulong _wrapGeneration;
    private ulong _visualRevision;
    private ulong _visualWrapGeneration;
    private BScanStoreVisualState _visualState;
    private BScanStoreVisualState? _writeBaselineVisualState;
    private int _activeLeases;
    private int _activeWriters;
    private int _maximumLeases;
    private ulong _accepted, _rejectedPinned, _evictions, _superseded;

    internal BScanTraceStore(int sampleCount, BScanOptions options, IShortArrayPool? pool = null,
        BoundedBScanVisualBufferPool? visualBuffers = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        _sampleCount = sampleCount;
        _bucketSamples = options.IndexBucketSamples;
        _bucketCount = checked((sampleCount + _bucketSamples - 1) / _bucketSamples);
        _pool = pool ?? SharedShortArrayPool.Instance;
        _visualBuffers = visualBuffers ?? new(options.VisualMemoryBudgetBytes);
        _ownsVisualBuffers = visualBuffers is null;
        var blocks = new List<Block>();
        long rentedBytes = 0;
        int columns = 0;
        try
        {
            const int indexedBucketBytes = sizeof(short) * 4;
            long fixedVisualBytes = checked(
                checked((long)_bucketCount * indexedBucketBytes) +
                checked((long)sampleCount * sizeof(short) * VisualWriteHeadroom) +
                checked(2L * 1024 * 1024));
            long availableVisualBytes = options.VisualMemoryBudgetBytes - fixedVisualBytes;
            long visualBytesPerColumn = checked(128L + checked((long)_bucketCount * indexedBucketBytes));
            int visualCapacity = (int)Math.Min(options.MaximumColumns,
                Math.Max(0, availableVisualBytes) / visualBytesPerColumn);
            if (visualCapacity < options.MinimumColumns)
            {
                throw new InvalidOperationException("The visual memory budget cannot hold the required slots and index.");
            }
            int standardColumns = Math.Max(1, Math.Min(64, 524_288 / sampleCount));
            while (columns < visualCapacity)
            {
                int blockColumns = Math.Min(standardColumns, visualCapacity - columns);
                int requestedLength = checked(sampleCount * blockColumns);
                short[] array = _pool.Rent(requestedLength);
                if (array.Length < requestedLength)
                {
                    _pool.Return(array);
                    throw new InvalidOperationException("The sample pool returned an undersized block.");
                }

                long bytes = checked((long)array.Length * sizeof(short));
                if (checked(rentedBytes + bytes) > options.TraceMemoryBudgetBytes)
                {
                    _pool.Return(array);
                    break;
                }

                blocks.Add(new(array, blockColumns));
                rentedBytes += bytes;
                columns += blockColumns;
            }

            if (columns < options.MinimumColumns)
            {
                throw new InvalidOperationException(
                    $"The trace budget admits {columns} columns, below the required {options.MinimumColumns}.");
            }

            _blocks = [.. blocks];
            _slots = new Slot[columns];
            int slotIndex = 0;
            for (int blockIndex = 0; blockIndex < _blocks.Length; blockIndex++)
            {
                for (int column = 0; column < _blocks[blockIndex].Columns; column++)
                {
                    IBScanVisualBufferOwner<BScanIndexedMinMax> indexOwner =
                        _visualBuffers.Rent<BScanIndexedMinMax>(_bucketCount,
                            BScanVisualBufferKind.StoreIndex);
                    _slotIndexOwners.Add(indexOwner);
                    _slots[slotIndex++] = new(blockIndex, checked(column * sampleCount), indexOwner.Array);
                }
            }
            RentedBytes = rentedBytes;
            LogicalBytes = checked((long)columns * sampleCount * sizeof(short));
            VisualLogicalBytes = checked(fixedVisualBytes + checked((long)columns * visualBytesPerColumn));
            _visualState = new(0, 0, _sampleCount, _bucketSamples, null, true);
        }
        catch (Exception original)
        {
            var cleanup = new List<Exception>();
            foreach (Block block in blocks)
            {
                try { _pool.Return(block.Samples); }
                catch (Exception error) { cleanup.Add(error); }
            }
            foreach (IBScanVisualBufferOwner<BScanIndexedMinMax> owner in _slotIndexOwners)
            { owner.Dispose(); }
            _slotIndexOwners.Clear();
            if (cleanup.Count != 0) { throw new AggregateException([original, .. cleanup]); }
            throw;
        }
    }

    internal int Capacity => _slots.Length;
    internal int BucketCount => _bucketCount;
    internal int BlocksRented => _blocks.Length;
    internal int BlocksReturned { get; private set; }
    internal long LogicalBytes { get; }
    internal long RentedBytes { get; }
    internal long VisualLogicalBytes { get; }

    internal StoreWriteReservation BeginWrite(int sampleLength)
    {
        lock (_gate)
        {
            if (!_accepting) { return StoreWriteReservation.Closed; }
            if (sampleLength != _sampleCount) { return StoreWriteReservation.Configuration; }
            Slot slot = _slots[_nextSlot];
            if (slot.Leases != 0 || slot.Writing)
            {
                _rejectedPinned++;
                return StoreWriteReservation.Pinned;
            }
            ulong nextGeneration = checked(slot.Generation + 1);
            slot.Writing = true;
            _activeWriters++;
            _writeBaselineVisualState = _visualState;
            Volatile.Write(ref _visualState, _visualState with { IsComplete = false });
            return new(this, _nextSlot, nextGeneration,
                _blocks[slot.BlockIndex].Samples.AsMemory(slot.Offset, _sampleCount));
        }
    }

    private StoreWriteResult CompleteWrite(int slotIndex, ulong generation, BScanTraceDescriptor uncommitted)
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            Slot slot = _slots[slotIndex];
            bool visualChanged = false;
            try
            {
                if (!_accepting || !slot.Writing || generation != checked(slot.Generation + 1))
                {
                    return StoreWriteResult.Closed;
                }
                if (slot.Committed)
                {
                    if (slot.IndexedGeneration != slot.Generation) { _superseded++; }
                    AddEvicted(slot.Descriptor!.Identity);
                    _evictions++;
                }
                slot.Generation = generation;
                _revision = checked(_revision + 1);
                slot.Descriptor = uncommitted with { StoreRevision = _revision, Generation = generation };
                slot.AcceptedWrapGeneration = _wrapGeneration;
                slot.Committed = true;
                slot.IndexedGeneration = 0;
                _nextSlot = (_nextSlot + 1) % _slots.Length;
                if (_nextSlot == 0) { _wrapGeneration = checked(_wrapGeneration + 1); }
                _accepted++;
                visualChanged = true;
                return new(StoreWriteKind.Accepted, slot.Descriptor);
            }
            finally
            {
                slot.Writing = false;
                _activeWriters--;
                Volatile.Write(ref _visualState, visualChanged
                    ? CreateVisualStateUnderLock()
                    : _writeBaselineVisualState ?? CreateVisualStateUnderLock());
                _writeBaselineVisualState = null;
                if (_activeWriters == 0) { drain = _writerDrain; }
                drain?.TrySetResult();
            }
        }
    }

    private void AbortWrite(int slotIndex, ulong generation)
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            Slot slot = _slots[slotIndex];
            if (!slot.Writing || generation != checked(slot.Generation + 1)) { return; }
            slot.Writing = false;
            _activeWriters--;
            Volatile.Write(ref _visualState,
                _writeBaselineVisualState ?? CreateVisualStateUnderLock());
            _writeBaselineVisualState = null;
            if (_activeWriters == 0) { drain = _writerDrain; }
        }
        drain?.TrySetResult();
    }

    internal BScanTraceLease? TryAcquire(BScanTraceIdentity identity, ulong? generation = null)
    {
        lock (_gate)
        {
            if (!_leasesAllowed) { return null; }
            for (int index = 0; index < _slots.Length; index++)
            {
                Slot slot = _slots[index];
                if (slot.Writing || !slot.Committed || slot.Descriptor!.Identity != identity ||
                    generation is { } expected && slot.Generation != expected) { continue; }
                slot.Leases++;
                _activeLeases++;
                _maximumLeases = Math.Max(_maximumLeases, _activeLeases);
                return new(this, index, slot.Generation, slot.Descriptor,
                    _blocks[slot.BlockIndex].Samples.AsMemory(slot.Offset, _sampleCount), slot.Index);
            }
            return null;
        }
    }

    internal BScanResidence GetResidence(BScanTraceIdentity identity)
    {
        lock (_gate)
        {
            if (_slots.Any(slot => slot.Committed && slot.Descriptor!.Identity == identity))
            {
                return BScanResidence.Available;
            }
            return _evicted.Contains(identity) ? BScanResidence.Evicted : BScanResidence.NotAvailable;
        }
    }

    internal int CaptureIndexCandidates(Span<BScanIndexCandidate> destination)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (Slot slot in _slots)
            {
                if (!slot.Committed || slot.IndexedGeneration == slot.Generation) { continue; }
                if (count == destination.Length) { break; }
                destination[count++] = new(slot.Descriptor!.Identity, slot.Generation);
            }
            return count;
        }
    }

    internal bool CommitIndex(BScanTraceIdentity identity, ulong generation, ReadOnlySpan<BScanIndexedMinMax> buckets)
    {
        lock (_gate)
        {
            Slot? slot = null;
            foreach (Slot candidate in _slots)
            {
                if (candidate.Committed && candidate.Descriptor!.Identity == identity &&
                    candidate.Generation == generation) { slot = candidate; break; }
            }
            if (slot is null)
            {
                _superseded++;
                return false;
            }
            if (buckets.Length != slot.Index.Length) { throw new ArgumentException("Invalid index length.", nameof(buckets)); }
            buckets.CopyTo(slot.Index);
            slot.IndexedGeneration = generation;
            slot.VisualRevision = checked(++_visualRevision);
            _visualWrapGeneration = Math.Max(_visualWrapGeneration, slot.AcceptedWrapGeneration);
            Volatile.Write(ref _visualState, CreateVisualStateUnderLock());
            return true;
        }
    }

    internal BScanStoreVisualState CurrentVisualState
    {
        get => Volatile.Read(ref _visualState);
    }

    internal BScanMinMax[]? CopyIndex(BScanTraceIdentity identity)
    {
        lock (_gate)
        {
            Slot? slot = null;
            foreach (Slot candidate in _slots)
            {
                if (candidate.Committed && candidate.Descriptor!.Identity == identity) { slot = candidate; break; }
            }
            if (slot is null || slot.IndexedGeneration != slot.Generation) { return null; }
            var result = new BScanMinMax[slot.Index.Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = new(slot.Index[index].Minimum, slot.Index[index].Maximum);
            }
            return result;
        }
    }

    internal bool TryResolveIdentity(ulong acquisitionIndex, out BScanTraceIdentity identity)
    {
        lock (_gate)
        {
            foreach (Slot slot in _slots)
            {
                if (slot.Committed && !slot.Writing && slot.Descriptor?.AcquisitionIndex == acquisitionIndex)
                { identity = slot.Descriptor.Identity; return true; }
            }
        }
        identity = default;
        return false;
    }

    internal void RecordSuperseded()
    {
        lock (_gate) { _superseded++; }
    }

    internal Action<BScanVisualLeaseCaptureStage, int>? VisualLeaseCaptureProbe { get; set; }

    internal BScanVisualLeaseBatch? CaptureVisualLeases(BScanStoreVisualState expected,
        CancellationToken cancellationToken, out BScanStoreVisualState state)
    {
        lock (_gate)
        {
            state = expected;
            if (!_leasesAllowed || !ReferenceEquals(_visualState, expected) || !expected.IsComplete)
            {
                return null;
            }
            var captured = new BScanTraceLease?[_slots.Length];
            int count = 0;
            try
            {
                for (int slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Slot slot = _slots[slotIndex];
                    if (!slot.Committed || slot.Writing || slot.IndexedGeneration != slot.Generation) { continue; }
                    VisualLeaseCaptureProbe?.Invoke(BScanVisualLeaseCaptureStage.BeforeCapture, count);
                    BScanTraceDescriptor descriptor = slot.Descriptor!;
                    BScanTraceLease lease;
                    if (IsWriteHeadroomSlot(slotIndex))
                    {
                        lease = CaptureDetachedLease(slot, descriptor, count);
                    }
                    else
                    {
                        slot.Leases++;
                        _activeLeases++;
                        _maximumLeases = Math.Max(_maximumLeases, _activeLeases);
                        lease = new(this, slotIndex, slot.Generation, descriptor,
                            _blocks[slot.BlockIndex].Samples.AsMemory(slot.Offset, _sampleCount), slot.Index,
                            slot.VisualRevision);
                        try
                        { VisualLeaseCaptureProbe?.Invoke(BScanVisualLeaseCaptureStage.AfterLeaseAcquired, count); }
                        catch (Exception original)
                        { BScanVisualLeaseBatch.ThrowAfterCleanup(original, [lease]); throw; }
                    }
                    captured[count] = lease;
                    count++;
                    VisualLeaseCaptureProbe?.Invoke(BScanVisualLeaseCaptureStage.AfterCapture, count);
                }
                state = _visualState;
                return new(captured, count);
            }
            catch (Exception original)
            {
                BScanVisualLeaseBatch.ThrowAfterCleanup(original, captured.AsSpan(0, count));
                throw;
            }
        }
    }

    internal BScanVisualLeaseBatch? CaptureVisualLeases(CancellationToken cancellationToken,
        out BScanStoreVisualState state) =>
        CaptureVisualLeases(CurrentVisualState, cancellationToken, out state);

    private BScanTraceLease CaptureDetachedLease(Slot slot, BScanTraceDescriptor descriptor, int capturedCount)
    {
        IBScanVisualBufferOwner<short>? samplesOwner = null;
        IBScanVisualBufferOwner<BScanIndexedMinMax>? indexOwner = null;
        try
        {
            samplesOwner = _visualBuffers.Rent<short>(_sampleCount, BScanVisualBufferKind.DetachedSamples);
            indexOwner = _visualBuffers.Rent<BScanIndexedMinMax>(_bucketCount, BScanVisualBufferKind.DetachedIndex);
            short[] samples = samplesOwner.Array;
            BScanIndexedMinMax[] index = indexOwner.Array;
            _blocks[slot.BlockIndex].Samples.AsSpan(slot.Offset, _sampleCount).CopyTo(samples);
            slot.Index.AsSpan().CopyTo(index);
            IBScanVisualBufferOwner<short> transferredSamples = samplesOwner;
            IBScanVisualBufferOwner<BScanIndexedMinMax> transferredIndex = indexOwner;
            var lease = new BScanTraceLease(descriptor, samples.AsMemory(0, _sampleCount),
                index.AsMemory(0, _bucketCount), slot.VisualRevision, () =>
                {
                    List<Exception>? errors = null;
                    try { transferredIndex.Dispose(); }
                    catch (Exception error) { (errors ??= []).Add(error); }
                    try { transferredSamples.Dispose(); }
                    catch (Exception error) { (errors ??= []).Add(error); }
                    if (errors is not null) { throw new AggregateException(errors); }
                });
            VisualLeaseCaptureProbe?.Invoke(BScanVisualLeaseCaptureStage.BeforeOwnerTransfer, capturedCount);
            samplesOwner = null;
            indexOwner = null;
            return lease;
        }
        catch (Exception original)
        {
            var errors = new List<Exception>();
            BScanVisualLeaseBatch.AddFlattened(errors, original);
            if (indexOwner is not null) try { indexOwner.Dispose(); }
                catch (Exception error) { BScanVisualLeaseBatch.AddFlattened(errors, error); }
            if (samplesOwner is not null) try { samplesOwner.Dispose(); }
                catch (Exception error) { BScanVisualLeaseBatch.AddFlattened(errors, error); }
            if (errors.Count == 1)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw(); }
            throw new AggregateException(errors);
        }
    }

    private BScanStoreVisualState CreateVisualStateUnderLock()
    {
        ulong minimum = ulong.MaxValue, maximum = 0;
        bool any = false;
        foreach (Slot slot in _slots)
        {
            if (!slot.Committed || slot.Writing || slot.IndexedGeneration != slot.Generation) { continue; }
            any = true;
            minimum = Math.Min(minimum, slot.Descriptor!.AcquisitionIndex);
            maximum = Math.Max(maximum, slot.Descriptor.AcquisitionIndex);
        }
        return new(_visualRevision, _visualWrapGeneration, _sampleCount, _bucketSamples,
            any ? new(minimum, maximum) : null, HasCompleteVisualStateUnderLock());
    }

    private bool IsWriteHeadroomSlot(int slotIndex)
    {
        int distance = slotIndex - _nextSlot;
        if (distance < 0) { distance += _slots.Length; }
        return distance < Math.Min(VisualWriteHeadroom, _slots.Length);
    }

    internal bool IsVisualStateCurrent(ulong revision, ulong wrapGeneration)
    {
        lock (_gate)
        {
            return HasCompleteVisualStateUnderLock() && _visualRevision == revision &&
                _visualWrapGeneration == wrapGeneration;
        }
    }

    internal bool IsBootstrapStateCompatible(ulong revision, ulong wrapGeneration)
    {
        lock (_gate)
        {
            return HasCompleteVisualStateUnderLock() && _visualRevision >= revision &&
                _visualWrapGeneration == wrapGeneration;
        }
    }

    private bool HasCompleteVisualStateUnderLock() => _leasesAllowed && _activeWriters == 0 &&
        !_slots.Any(slot => slot.Committed && slot.IndexedGeneration != slot.Generation);

    internal Task CloseAsync()
    {
        lock (_gate)
        {
            _accepting = false;
            _leasesAllowed = false;
            Volatile.Write(ref _visualState, _visualState with { IsComplete = false });
            return _closeTask ??= CloseCoreAsync();
        }
    }

    internal void StopAccepting()
    {
        lock (_gate) { _accepting = false; }
    }

    internal bool IsAccepting { get { lock (_gate) { return _accepting; } } }

    internal Task WaitForWritersAsync()
    {
        lock (_gate)
        {
            return _activeWriters == 0 ? Task.CompletedTask :
                (_writerDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private async Task CloseCoreAsync()
    {
        Task writers;
        Task leases;
        lock (_gate)
        {
            writers = _activeWriters == 0 ? Task.CompletedTask :
                (_writerDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            leases = _activeLeases == 0 ? Task.CompletedTask :
                (_leaseDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await Task.WhenAll(writers, leases).ConfigureAwait(false);
        bool disposeOwnedVisualBuffers = false;
        lock (_gate)
        {
            if (_returned) { return; }
            var errors = new List<Exception>();
            foreach (Block block in _blocks)
            {
                if (block.ReturnAttempted) { continue; }
                block.ReturnAttempted = true;
                try
                {
                    _pool.Return(block.Samples);
                    BlocksReturned++;
                }
                catch (Exception error) { errors.Add(error); }
            }
            foreach (IBScanVisualBufferOwner<BScanIndexedMinMax> owner in _slotIndexOwners)
            { owner.Dispose(); }
            _slotIndexOwners.Clear();
            _returned = errors.Count == 0 && BlocksReturned == BlocksRented;
            disposeOwnedVisualBuffers = _ownsVisualBuffers;
            if (errors.Count != 0) { throw new AggregateException(errors); }
        }
        if (disposeOwnedVisualBuffers) { await _visualBuffers.DisposeAsync().ConfigureAwait(false); }
    }

    internal StoreStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new(_accepted, _rejectedPinned, _evictions, _superseded, _activeLeases,
                    _maximumLeases, BlocksRented, BlocksReturned, LogicalBytes, RentedBytes,
                    VisualLogicalBytes, Capacity, _returned);
            }
        }
    }

    private void ReleaseLease(int slotIndex, ulong generation)
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            Slot slot = _slots[slotIndex];
            if (slot.Generation != generation || slot.Leases <= 0) { throw new InvalidOperationException("Invalid trace lease release."); }
            slot.Leases--;
            _activeLeases--;
            if (_activeLeases == 0) { drain = _leaseDrain; }
        }
        drain?.TrySetResult();
    }

    private void AddEvicted(BScanTraceIdentity identity)
    {
        if (_evicted.Add(identity)) { _evictionOrder.Enqueue(identity); }
        while (_evictionOrder.Count > _slots.Length)
        {
            _evicted.Remove(_evictionOrder.Dequeue());
        }
    }

    internal sealed class BScanTraceLease : IDisposable
    {
        private BScanTraceStore? _store;
        private Action? _releaseDetached;
        private readonly int _slot;
        private readonly ulong _generation;

        internal BScanTraceLease(BScanTraceStore store, int slot, ulong generation,
            BScanTraceDescriptor descriptor, ReadOnlyMemory<short> samples, ReadOnlyMemory<BScanIndexedMinMax> index,
            ulong visualRevision = 0)
        {
            _store = store;
            _slot = slot;
            _generation = generation;
            Descriptor = descriptor;
            Samples = samples;
            Index = index;
            VisualRevision = visualRevision;
        }

        internal BScanTraceLease(BScanTraceDescriptor descriptor, ReadOnlyMemory<short> samples,
            ReadOnlyMemory<BScanIndexedMinMax> index, ulong visualRevision, Action releaseDetached)
        {
            Descriptor = descriptor;
            Samples = samples;
            Index = index;
            VisualRevision = visualRevision;
            _releaseDetached = releaseDetached;
        }

        internal BScanTraceDescriptor Descriptor { get; }
        internal ReadOnlyMemory<short> Samples { get; }
        internal ReadOnlyMemory<BScanIndexedMinMax> Index { get; }
        internal ulong VisualRevision { get; }
        public void Dispose()
        {
            Interlocked.Exchange(ref _store, null)?.ReleaseLease(_slot, _generation);
            Interlocked.Exchange(ref _releaseDetached, null)?.Invoke();
        }
    }

    private sealed class Block(short[] samples, int columns)
    {
        internal short[] Samples { get; } = samples;
        internal int Columns { get; } = columns;
        internal bool ReturnAttempted;
    }
    private sealed class Slot(int blockIndex, int offset, BScanIndexedMinMax[] index)
    {
        internal int BlockIndex { get; } = blockIndex;
        internal int Offset { get; } = offset;
        internal BScanTraceDescriptor? Descriptor;
        internal ulong Generation;
        internal ulong IndexedGeneration;
        internal ulong VisualRevision;
        internal ulong AcceptedWrapGeneration;
        internal int Leases;
        internal bool Committed;
        internal bool Writing;
        internal BScanIndexedMinMax[] Index { get; } = index;
    }

    internal sealed class StoreWriteReservation
    {
        private BScanTraceStore? _store;
        private readonly int _slotIndex;
        private readonly ulong _generation;

        private StoreWriteReservation(StoreWriteKind kind)
        {
            Kind = kind;
        }

        internal StoreWriteReservation(BScanTraceStore store, int slotIndex, ulong generation, Memory<short> destination)
        {
            _store = store;
            _slotIndex = slotIndex;
            _generation = generation;
            Destination = destination;
            Kind = StoreWriteKind.Accepted;
        }

        internal static StoreWriteReservation Pinned { get; } = new(StoreWriteKind.Pinned);
        internal static StoreWriteReservation Configuration { get; } = new(StoreWriteKind.Configuration);
        internal static StoreWriteReservation Closed { get; } = new(StoreWriteKind.Closed);
        internal StoreWriteKind Kind { get; }
        internal Memory<short> Destination { get; }

        internal StoreWriteResult Commit(BScanTraceDescriptor descriptor)
        {
            BScanTraceStore? store = Interlocked.Exchange(ref _store, null);
            return store is null ? StoreWriteResult.Closed : store.CompleteWrite(_slotIndex, _generation, descriptor);
        }

        internal void Abort()
        {
            BScanTraceStore? store = Interlocked.Exchange(ref _store, null);
            if (store is not null) { store.AbortWrite(_slotIndex, _generation); }
        }
    }
}

internal enum StoreWriteKind { Accepted, Pinned, Configuration, Closed }
internal readonly record struct StoreWriteResult(StoreWriteKind Kind, BScanTraceDescriptor? Descriptor = null)
{
    internal static StoreWriteResult Pinned => new(StoreWriteKind.Pinned);
    internal static StoreWriteResult Configuration => new(StoreWriteKind.Configuration);
    internal static StoreWriteResult Closed => new(StoreWriteKind.Closed);
}
internal enum BScanResidence { Available, NotAvailable, Evicted }
internal readonly record struct BScanIndexCandidate(BScanTraceIdentity Identity, ulong Generation);
internal readonly record struct BScanIndexedMinMax(
    short Minimum, ushort MinimumOffset, short Maximum, ushort MaximumOffset);
internal sealed record BScanStoreVisualState(
    ulong StoreRevision,
    ulong WrapGeneration,
    int SampleCount,
    int IndexBucketSamples,
    BScanAcquisitionRange? ResidentRange,
    bool IsComplete);
internal readonly record struct StoreStatistics(
    ulong Accepted, ulong RejectedPinned, ulong Evicted, ulong Superseded,
    int ActiveLeases, int MaximumLeases, int BlocksRented, int BlocksReturned,
    long LogicalBytes, long RentedBytes, long VisualLogicalBytes, int Capacity, bool Closed);

internal enum BScanVisualLeaseCaptureStage
{
    BeforeCapture,
    AfterLeaseAcquired,
    BeforeOwnerTransfer,
    AfterCapture
}

internal sealed class BScanVisualLeaseBatch : IDisposable
{
    private BScanTraceStore.BScanTraceLease?[]? _leases;

    internal BScanVisualLeaseBatch(BScanTraceStore.BScanTraceLease?[] leases, int count)
    {
        _leases = leases;
        Count = count;
    }

    internal int Count { get; }
    internal BScanTraceStore.BScanTraceLease?[] Items =>
        _leases ?? throw new ObjectDisposedException(nameof(BScanVisualLeaseBatch));

    public void Dispose() => Dispose(null);

    internal void Dispose(Exception? primary) => DisposeCore(primary, true);

    internal void DisposeAfterSwallowed(Exception primary) => DisposeCore(primary, false);

    private void DisposeCore(Exception? primary, bool rethrowPrimary)
    {
        BScanTraceStore.BScanTraceLease?[]? leases = Interlocked.Exchange(ref _leases, null);
        if (leases is null)
        {
            if (primary is not null && rethrowPrimary)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); }
            return;
        }
        Cleanup(primary, leases.AsSpan(0, Count), rethrowPrimary);
    }

    internal static void ThrowAfterCleanup(Exception? primary,
        Span<BScanTraceStore.BScanTraceLease?> leases)
        => Cleanup(primary, leases, true);

    private static void Cleanup(Exception? primary,
        Span<BScanTraceStore.BScanTraceLease?> leases, bool rethrowPrimary)
    {
        var errors = new List<Exception>();
        if (primary is not null) { AddFlattened(errors, primary); }
        int primaryCount = errors.Count;
        for (int index = leases.Length - 1; index >= 0; index--)
        {
            BScanTraceStore.BScanTraceLease? lease = leases[index];
            leases[index] = null;
            if (lease is null) { continue; }
            try { lease.Dispose(); }
            catch (Exception error) { AddFlattened(errors, error); }
        }
        if (errors.Count == 0 || !rethrowPrimary && errors.Count == primaryCount) { return; }
        if (errors.Count == 1 && primary is not null && rethrowPrimary)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); }
        throw new AggregateException(errors);
    }

    internal static void AddFlattened(List<Exception> errors, Exception error)
    {
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions) { AddFlattened(errors, inner); }
        }
        else { errors.Add(error); }
    }
}
