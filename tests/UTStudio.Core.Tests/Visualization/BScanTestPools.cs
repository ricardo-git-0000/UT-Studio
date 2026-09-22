using System.Buffers;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

internal sealed class DeterministicShortPool(Func<int, int>? length = null, int? throwOnRent = null) : IShortArrayPool
{
    private readonly HashSet<short[]> _outstanding = [];
    internal int RentCount { get; private set; }
    internal int ReturnCount { get; private set; }
    internal IReadOnlyCollection<short[]> Outstanding => _outstanding;
    internal HashSet<int> ThrowOnReturns { get; } = [];

    public short[] Rent(int minimumLength)
    {
        RentCount++;
        if (throwOnRent == RentCount) { throw new InvalidOperationException("synthetic rent failure"); }
        var array = new short[length?.Invoke(minimumLength) ?? minimumLength];
        _outstanding.Add(array);
        return array;
    }

    public void Return(short[] array)
    {
        Assert.IsTrue(_outstanding.Remove(array), "Array returned twice or not rented by this pool.");
        ReturnCount++;
        if (ThrowOnReturns.Contains(ReturnCount)) { throw new InvalidOperationException("synthetic return failure"); }
    }
}

internal sealed class DeterministicOwnerPool : ITraceCopyOwnerPool
{
    internal int RentCount { get; private set; }
    internal int ReturnCount { get; private set; }
    internal bool ThrowOnReturn { get; set; }
    internal Action? BeforeReturn { get; set; }
    public IMemoryOwner<short> Rent(int minimumLength)
    {
        RentCount++;
        return new Owner(new short[minimumLength], () =>
        {
            BeforeReturn?.Invoke();
            ReturnCount++;
            if (ThrowOnReturn) { throw new InvalidOperationException("synthetic owner return failure"); }
        });
    }

    private sealed class Owner(short[] samples, Action returned) : IMemoryOwner<short>
    {
        private short[]? _samples = samples;
        public Memory<short> Memory => _samples ?? throw new ObjectDisposedException(nameof(Owner));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _samples, null) is not null) { returned(); }
        }
    }
}
