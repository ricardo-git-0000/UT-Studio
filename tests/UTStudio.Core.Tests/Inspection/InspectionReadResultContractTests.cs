using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;

namespace UTStudio.Core.Tests.Inspection;

[TestClass]
public sealed class InspectionReadResultContractTests
{
    [TestMethod]
    public void Defaults_are_unknown_and_never_available()
    {
        Assert.AreEqual(InspectionReadStatus.Unknown, default(InspectionTraceReadResult).Status);
        Assert.IsNull(default(InspectionTraceReadResult).Lease);
        Assert.AreEqual(InspectionReadStatus.Unknown, default(InspectionRangeItem).Status);
        Assert.IsNull(default(InspectionRangeItem).Lease);
    }

    [TestMethod]
    public void Factories_enforce_available_and_unavailable_invariants()
    {
        var lease = new TestLease();
        InspectionTraceReadResult available = InspectionTraceReadResult.Available(new(1), lease);
        Assert.AreEqual(InspectionReadStatus.Available, available.Status);
        Assert.AreSame(lease, available.Lease);
        Assert.ThrowsExactly<ArgumentException>(() =>
            InspectionTraceReadResult.Available(InspectionRevision.Initial, lease));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            InspectionTraceReadResult.Unavailable(InspectionReadStatus.Available, new(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            InspectionTraceReadResult.Unavailable((InspectionReadStatus)999, new(1)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            InspectionTraceReadResult.Unavailable(InspectionReadStatus.Faulted, new(1)));

        InspectionRangeItem item = InspectionRangeItem.Available(lease);
        Assert.AreEqual(lease.Descriptor.Identity, item.Identity);
        Assert.ThrowsExactly<ArgumentException>(() => InspectionRangeReadResult.Page(
            InspectionRevision.Initial, [item]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            InspectionRangeItem.Unavailable(lease.Descriptor.Identity, InspectionReadStatus.Cancelled));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Range_dispose_continues_after_a_single_failure_at_any_position(int failingIndex)
    {
        var leases = Enumerable.Range(0, 3).Select(index => new TestLease(index, failure:
            index == failingIndex ? new InvalidOperationException($"failure-{index}") : null)).ToArray();
        InspectionRangeReadResult range = InspectionRangeReadResult.Page(new(1),
            leases.Select(InspectionRangeItem.Available).ToArray());

        Assert.ThrowsExactly<InvalidOperationException>(range.Dispose);

        Assert.IsTrue(leases.All(static lease => lease.DisposeCalls == 1));
        Assert.IsEmpty(range.Items);
    }

    [TestMethod]
    public void Range_dispose_attempts_every_lease_in_reverse_and_is_idempotent()
    {
        var order = new List<int>();
        var first = new TestLease(1, order, new InvalidOperationException("first"));
        var middle = new TestLease(2, order, new AggregateException(
            new ArgumentException("middle-a"), new ApplicationException("middle-b")));
        var last = new TestLease(3, order, new NotSupportedException("last"));
        InspectionRangeReadResult range = InspectionRangeReadResult.Page(new(3),
            [InspectionRangeItem.Available(first), InspectionRangeItem.Available(middle),
                InspectionRangeItem.Available(last)]);

        InspectionRangeDisposeException error = Assert.ThrowsExactly<InspectionRangeDisposeException>(range.Dispose);
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, order);
        Assert.IsInstanceOfType<NotSupportedException>(error.PrimaryException);
        Assert.HasCount(3, error.SecondaryExceptions);
        range.Dispose();
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, middle.DisposeCalls);
        Assert.AreEqual(1, last.DisposeCalls);
    }

    [TestMethod]
    public async Task Concurrent_range_dispose_attempts_each_lease_once()
    {
        var first = new TestLease();
        var second = new TestLease();
        InspectionRangeReadResult range = InspectionRangeReadResult.Page(new(2),
            [InspectionRangeItem.Available(first), InspectionRangeItem.Available(second)]);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(range.Dispose)));
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, second.DisposeCalls);
    }

    [TestMethod]
    public async Task Range_dispose_atomically_retires_items_before_releasing_leases()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = new TestLease();
        var blocking = new BlockingLease(entered, release);
        InspectionRangeReadResult range = InspectionRangeReadResult.Page(new(2),
            [InspectionRangeItem.Available(first), InspectionRangeItem.Available(blocking)]);

        Task dispose = Task.Run(range.Dispose);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.IsEmpty(range.Items);
            Assert.IsFalse(dispose.IsCompleted);
        }
        finally { release.Set(); }
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, first.DisposeCalls);
        Assert.AreEqual(1, blocking.DisposeCalls);
    }

    private sealed class TestLease : IInspectionTraceLease
    {
        private readonly int _id;
        private readonly List<int>? _order;
        private readonly Exception? _failure;
        private int _disposeCalls;

        internal TestLease(int id = 0, List<int>? order = null, Exception? failure = null)
        {
            _id = id;
            _order = order;
            _failure = failure;
            var stream = new UtStreamIdentity(new(Guid.Parse("10000000-0000-0000-0000-000000000001")),
                new(Guid.Parse("20000000-0000-0000-0000-000000000002")), new("contracts"), new(1));
            Descriptor = new(new(stream, (ulong)id), (ulong)id, TimeSpan.Zero, DateTimeOffset.UnixEpoch,
                null, 1, 1_000_000, 0, UtSignalMode.Rf);
        }

        public TraceDescriptor Descriptor { get; }
        public IReadOnlyUtSamples Samples { get; } = new Samples();
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            _order?.Add(_id);
            if (_failure is not null) { throw _failure; }
        }
    }

    private sealed class Samples : IReadOnlyUtSamples
    {
        public int Count => 1;
        public short this[int index] => index == 0 ? (short)0 : throw new ArgumentOutOfRangeException(nameof(index));
        public void CopyTo(Span<short> destination) { destination[0] = 0; }
    }

    private sealed class BlockingLease(TaskCompletionSource entered,
        ManualResetEventSlim release) : IInspectionTraceLease
    {
        private readonly TestLease _inner = new();
        public TraceDescriptor Descriptor => _inner.Descriptor;
        public IReadOnlyUtSamples Samples => _inner.Samples;
        internal int DisposeCalls => _inner.DisposeCalls;
        public void Dispose()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException("The range dispose lease gate was not released."); }
            _inner.Dispose();
        }
    }
}
