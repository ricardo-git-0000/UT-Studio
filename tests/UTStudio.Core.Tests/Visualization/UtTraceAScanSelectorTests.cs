using UTStudio.Contracts.Inspection;
using UTStudio.Core.Tests.Application;
using UTStudio.Core.Tests.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class UtTraceAScanSelectorTests
{
    [TestMethod]
    public async Task AvailableUsesExactIdentityCopiesBeforeReleaseAndReleasesExactlyOnce()
    {
        TraceIdentity identity = Identity(37);
        var lease = new TrackingLease(
            InspectionIdentityTests.Descriptor(identity, sequence: 9), [1, -2, 3, -4]);
        var provider = new ScriptedProvider((requested, _) =>
        {
            Assert.AreEqual(identity, requested);
            return ValueTask.FromResult(Result(InspectionReadStatus.Available, lease));
        });
        var selector = new UtTraceAScanSelector(provider);

        AScanSelectionResult result = await selector.ResolveAsync(identity, 42);

        Assert.AreEqual(BScanTraceResultKind.Available, result.Kind);
        Assert.AreEqual(identity, result.Snapshot!.TraceIdentity);
        Assert.AreEqual<ulong>(9, result.Snapshot.Sequence);
        Assert.AreEqual<ulong>(42, result.Snapshot.Version);
        Assert.AreEqual(1, provider.Started);
        Assert.AreEqual(1, provider.Completed);
        Assert.AreEqual(1, lease.DisposeCalls);
        Assert.AreEqual(0, lease.AccessAfterDispose);
    }

    [TestMethod]
    [DataRow(InspectionReadStatus.NotAvailable, BScanTraceResultKind.NotAvailable)]
    [DataRow(InspectionReadStatus.Gap, BScanTraceResultKind.Gap)]
    [DataRow(InspectionReadStatus.OutOfRange, BScanTraceResultKind.OutOfRange)]
    [DataRow(InspectionReadStatus.Cancelled, BScanTraceResultKind.Cancelled)]
    [DataRow(InspectionReadStatus.Closed, BScanTraceResultKind.Closed)]
    [DataRow(InspectionReadStatus.Faulted, BScanTraceResultKind.Faulted)]
    public async Task DiscriminatedProviderResultsRemainDistinct(
        InspectionReadStatus status, BScanTraceResultKind expected)
    {
        var provider = new ScriptedProvider((_, _) =>
            ValueTask.FromResult(Result(status, errorCode: status.ToString())));

        AScanSelectionResult result = await new UtTraceAScanSelector(provider)
            .ResolveAsync(Identity(1), 1);

        Assert.AreEqual(expected, result.Kind);
        Assert.AreEqual(status.ToString(), result.Error);
        Assert.AreEqual(provider.Started, provider.Completed);
    }

    [TestMethod]
    public async Task ProviderExceptionAndIncompatibleDescriptorFaultWithoutLeaking()
    {
        var throwing = new ScriptedProvider((_, _) =>
            ValueTask.FromException<InspectionTraceReadResult>(
                new InvalidOperationException("provider-failed")));
        AScanSelectionResult failure = await new UtTraceAScanSelector(throwing)
            .ResolveAsync(Identity(1), 1);
        Assert.AreEqual(BScanTraceResultKind.Faulted, failure.Kind);
        StringAssert.Contains(failure.Error, "provider-failed");

        var lease = new TrackingLease(
            InspectionIdentityTests.Descriptor(Identity(2), sequence: 99), [1, 2, 3, 4]);
        var incompatible = new ScriptedProvider((_, _) =>
            ValueTask.FromResult(Result(InspectionReadStatus.Available, lease)));
        AScanSelectionResult mismatch = await new UtTraceAScanSelector(incompatible)
            .ResolveAsync(Identity(1), 2);
        Assert.AreEqual(BScanTraceResultKind.Faulted, mismatch.Kind);
        Assert.AreEqual(1, lease.DisposeCalls);
        Assert.AreEqual(0, lease.AccessAfterDispose);
    }

    [TestMethod]
    public async Task CancellationWaitingForProviderCompletesOperationWithoutLease()
    {
        var entered = ManualSessionSource.Signal();
        var provider = new ScriptedProvider(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new AssertFailedException("Cancelled provider wait resumed unexpectedly.");
        });
        using var cancellation = new CancellationTokenSource();
        Task<AScanSelectionResult> pending = new UtTraceAScanSelector(provider)
            .ResolveAsync(Identity(1), 1, cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();

        Assert.AreEqual(BScanTraceResultKind.Cancelled, (await pending).Kind);
        Assert.AreEqual(provider.Started, provider.Completed);
    }

    [TestMethod]
    public async Task CancellationImmediatelyAfterLeaseAcquisitionReleasesExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var lease = new TrackingLease(
            InspectionIdentityTests.Descriptor(Identity(1)), [1, 2, 3, 4]);
        var provider = new ScriptedProvider((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(Result(InspectionReadStatus.Available, lease));
        });

        AScanSelectionResult result = await new UtTraceAScanSelector(provider)
            .ResolveAsync(Identity(1), 1, cancellation.Token);

        Assert.AreEqual(BScanTraceResultKind.Cancelled, result.Kind);
        Assert.AreEqual(1, lease.DisposeCalls);
        Assert.AreEqual(0, lease.AccessAfterDispose);
    }

    [TestMethod]
    public async Task ProjectionExceptionReleasesExactlyOnceAndNeverReadsDisposedLease()
    {
        var lease = new TrackingLease(
            InspectionIdentityTests.Descriptor(Identity(1)), [1, 2, 3, 4]);
        var provider = new ScriptedProvider((_, _) =>
            ValueTask.FromResult(Result(InspectionReadStatus.Available, lease)));

        AScanSelectionResult result = await new UtTraceAScanSelector(provider, new ThrowingProjector())
            .ResolveAsync(Identity(1), 1);

        Assert.AreEqual(BScanTraceResultKind.Faulted, result.Kind);
        Assert.AreEqual(1, lease.DisposeCalls);
        Assert.AreEqual(0, lease.AccessAfterDispose);
    }

    private static TraceIdentity Identity(ulong acquisitionIndex) =>
        new(InspectionIdentityTests.Stream(), acquisitionIndex);

    private static InspectionTraceReadResult Result(InspectionReadStatus status,
        IInspectionTraceLease? lease = null, string? errorCode = null) =>
        new(status, new InspectionRevision(7), lease, errorCode);

    private sealed class ScriptedProvider(
        Func<TraceIdentity, CancellationToken, ValueTask<InspectionTraceReadResult>> read)
        : IUtTraceProvider
    {
        internal int Started;
        internal int Completed;

        public async ValueTask<InspectionTraceReadResult> ReadAsync(TraceIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Started);
            try { return await read(identity, cancellationToken); }
            finally { Interlocked.Increment(ref Completed); }
        }
    }

    private sealed class TrackingLease(TraceDescriptor descriptor, short[] samples)
        : IInspectionTraceLease
    {
        private int _disposed;
        internal int DisposeCalls;
        internal int AccessAfterDispose;
        public TraceDescriptor Descriptor
        {
            get { CheckAlive(); return descriptor; }
        }
        public IReadOnlyUtSamples Samples { get; } = new TrackingSamples(samples);

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            Interlocked.Exchange(ref _disposed, 1);
            ((TrackingSamples)Samples).Dispose();
        }

        private void CheckAlive()
        {
            if (Volatile.Read(ref _disposed) == 0) return;
            Interlocked.Increment(ref AccessAfterDispose);
            throw new ObjectDisposedException(nameof(TrackingLease));
        }

        private sealed class TrackingSamples(short[] values) : IReadOnlyUtSamples
        {
            private int _disposed;
            public int Count { get { CheckAlive(); return values.Length; } }
            public short this[int index] { get { CheckAlive(); return values[index]; } }
            public void CopyTo(Span<short> destination)
            { CheckAlive(); values.CopyTo(destination); }
            internal void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            private void CheckAlive()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(TrackingSamples));
            }
        }
    }

    private sealed class ThrowingProjector : IAScanProjector
    {
        public AScanSnapshot Project(ConventionalUtFrameMetadata metadata, ulong sequence,
            TimeSpan elapsedSinceRunStart, ReadOnlySpan<short> samples, ulong version) =>
            throw new InvalidOperationException("projection-failed");
    }
}
