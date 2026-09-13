using System.Threading.Channels;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

internal static class VisualTestSupport
{
    internal static ConventionalUtFrameMetadata Metadata(int count = 2048, double rate = 50_000_000,
        double offset = 0, AcquisitionRunId? run = null) =>
        new(new UtSourceId("visual-test"), run ?? new AcquisitionRunId(Guid.NewGuid()),
            new(new PhysicalChannelId(0), count, rate, offset), DateTimeOffset.UnixEpoch);

    internal static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) { watchdog.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    internal sealed class Observer(Action<AScanSnapshot>? action = null) : IObserver<AScanSnapshot>
    {
        private readonly Channel<AScanSnapshot> _received = Channel.CreateUnbounded<AScanSnapshot>();
        internal async Task<AScanSnapshot> NextAsync() => await _received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void OnNext(AScanSnapshot value) { action?.Invoke(value); _received.Writer.TryWrite(value); }
        public void OnCompleted() { }
        public void OnError(Exception error) => Assert.Fail($"Unexpected OnError: {error}");
    }
}
