using System.Windows;
using UTStudio.Contracts.Application;
using UTStudio.Domain.Acquisition;
using UTStudio.Presentation;
using UTStudio.Visualization.Core;
namespace UTStudio.AScan.Wpf.Demo;

public partial class MainWindow : Window
{
    private readonly Feed<SessionSnapshot> _sessions = new();
    private readonly Feed<AScanSnapshot> _visuals = new();

    public MainWindow()
    {
        InitializeComponent();
        var run = new AcquisitionRunId(Guid.NewGuid());
        var source = new UtSourceId("ascan-demo");
        var session = new SessionSnapshot(1, SessionPhase.Running, source, run, 1, 1, 0, null, null, [], 0, canStop: true);
        Scan.Context = new AScanComponentContext(_sessions, session, _visuals);
        Loaded += (_, _) =>
        {
            _sessions.Publish(session);
            var configuration = new ConventionalAcquisitionConfiguration(new PhysicalChannelId(0), 512, 50_000_000);
            var metadata = new ConventionalUtFrameMetadata(source, run, configuration, DateTimeOffset.UtcNow);
            short[] samples = Enumerable.Range(0, 512).Select(i => (short)(Math.Sin(i * Math.PI / 32) * short.MaxValue)).ToArray();
            _visuals.Publish(new AScanProjector().Project(metadata, 0, TimeSpan.Zero, samples, 1));
        };
        Closed += async (_, _) => await Scan.CloseAsync();
    }

    private sealed class Feed<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        public IDisposable Subscribe(IObserver<T> observer) { _observers.Add(observer); return new Subscription(_observers, observer); }
        public void Publish(T value) { foreach (IObserver<T> observer in _observers.ToArray()) { observer.OnNext(value); } }
        private sealed class Subscription(List<IObserver<T>> observers, IObserver<T> observer) : IDisposable
        { public void Dispose() => observers.Remove(observer); }
    }
}
