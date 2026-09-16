using System.Threading.Channels;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using System.Runtime.CompilerServices;
using UTStudio.Core.Tests.TestDoubles;

namespace UTStudio.Core.Tests.Visualization;

internal static class VisualTestSupport
{
    internal static ConventionalUtFrameMetadata Metadata(int count = 2048, double rate = 50_000_000,
        double offset = 0, AcquisitionRunId? run = null) =>
        new(new UtSourceId("visual-test"), run ?? new AcquisitionRunId(Guid.NewGuid()),
            new(new PhysicalChannelId(0), count, rate, offset), DateTimeOffset.UnixEpoch);

    internal static Task Watch(Task task, [CallerArgumentExpression(nameof(task))] string condition = "visual operation") => DiagnosticWait.For(task, condition);

    internal static Task UntilAsync(Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string description = "visual condition") => DiagnosticWait.Until(condition, description);

    internal sealed class Observer(Action<AScanSnapshot>? action = null) : IObserver<AScanSnapshot>
    {
        private readonly Channel<AScanSnapshot> _received = Channel.CreateUnbounded<AScanSnapshot>();
        internal async Task<AScanSnapshot> NextAsync() => await DiagnosticWait.For(_received.Reader.ReadAsync().AsTask(), "observer received next visual snapshot");
        public void OnNext(AScanSnapshot value) { action?.Invoke(value); _received.Writer.TryWrite(value); }
        public void OnCompleted() { }
        public void OnError(Exception error) => Assert.Fail($"Unexpected OnError: {error}");
    }
}
