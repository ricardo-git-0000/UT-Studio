using BenchmarkDotNet.Attributes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.ExceptionServices;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Benchmarks;

[MemoryDiagnoser]
public class BScanWpfBenchmarks
{
    private readonly byte[] _full = new byte[1024 * 1024];
    private readonly byte[] _one = new byte[1024];
    private readonly byte[] _eight = new byte[8 * 1024];
    private readonly byte[] _bgra = new byte[1024 * 1024 * 4];
    private readonly uint[] _mapped = new uint[1024 * 1024];
    private BScanControl _fullControl = null!;
    private BScanControl _oneControl = null!;
    private BScanControl _eightControl = null!;
    private BScanControl _rollbackControl = null!;
    private RenderTargetBitmap _renderTarget = null!;
    private ulong _revision = 1;
    private StaHost _sta = null!;
    private Action _fullCommit = null!;
    private Action _incrementalOne = null!;
    private Action _incrementalEight = null!;
    private Action _rollback = null!;
    private Action _render = null!;

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < _full.Length; i++) _full[i] = (byte)(i % 256);
        Array.Fill(_one, (byte)128);
        Array.Fill(_eight, (byte)255);
        _sta = new();
        _sta.Invoke(SetupWpf);
    }

    private void SetupWpf()
    {
        _fullControl = CreateControl();
        _oneControl = CreateControl();
        _eightControl = CreateControl();
        _rollbackControl = CreateControl();
        ApplyInitial(_fullControl);
        ApplyInitial(_oneControl);
        ApplyInitial(_eightControl);
        ApplyInitial(_rollbackControl);
        _rollbackControl.FailIncrementalCommitAtRow = 512;
        _fullControl.SelectedAcquisitionIndex = 512;
        _fullControl.SelectionStatus = "Resident";
        _renderTarget = new(1024, 1024, 96, 96, PixelFormats.Pbgra32);
        _fullCommit = () => _fullControl.ApplyRaster(
            Snapshot(++_revision, 0, true, new(0, 0, 1024, 1024)), FillBgra);
        _incrementalOne = () =>
        {
            ulong basis = _oneControl.AppliedSnapshot!.StoreRevision;
            _oneControl.ApplyRaster(Snapshot(basis + 1, basis, false, new(3, 0, 1, 1024)), FillBgra);
        };
        _incrementalEight = () =>
        {
            ulong basis = _eightControl.AppliedSnapshot!.StoreRevision;
            _eightControl.ApplyRaster(Snapshot(basis + 1, basis, false, new(3, 0, 8, 1024)), FillBgra);
        };
        _rollback = () =>
        {
            ulong basis = _rollbackControl.AppliedSnapshot!.StoreRevision;
            try { _rollbackControl.ApplyRaster(Snapshot(basis + 1, basis, false, new(3, 0, 8, 1024)), FillBgra); }
            catch (InvalidOperationException) { }
        };
        _render = () => _renderTarget.Render(_fullControl);
    }

    [GlobalCleanup]
    public void Cleanup() => _sta.Dispose();

    [Benchmark]
    public void Full1024x1024Bgra32() =>
        BScanPalette.CopyToBgra32(_full, 1024, _bgra, 4096, new(0, 0, 1024, 1024));
    [Benchmark]
    public void Incremental1x1024Bgra32() =>
        BScanPalette.CopyToBgra32(_one, 1, _bgra, 4096, new(0, 0, 1, 1024));
    [Benchmark]
    public void Incremental8x1024Bgra32() =>
        BScanPalette.CopyToBgra32(_eight, 8, _bgra, 4096, new(0, 0, 8, 1024));

    [Benchmark]
    public uint IsolatedLut()
    {
        uint checksum = 0;
        for (int i = 0; i < _full.Length; i++) checksum ^= _mapped[i] = BScanPalette.Bgra32[_full[i]];
        return checksum;
    }

    [Benchmark]
    public void WriteableBitmapFullCommit() => _sta.Invoke(_fullCommit);

    [Benchmark]
    public void WriteableBitmapIncremental1x1024() => _sta.Invoke(_incrementalOne);

    [Benchmark]
    public void WriteableBitmapIncremental8x1024() => _sta.Invoke(_incrementalEight);

    [Benchmark]
    public void WriteableBitmapRollback() => _sta.Invoke(_rollback);

    [Benchmark]
    public void RenderBitmapAxesSelectionAndGap() => _sta.Invoke(_render);

    private static BScanControl CreateControl()
    {
        var control = new BScanControl
        {
            SpatialViewport = new(0, 0, 1023),
            TimeViewport = ScanTimeViewportOperations.Create(0, 1),
            ResidentRange = new(0, 1023)
        };
        control.Measure(new(1024, 1024));
        control.Arrange(new(0, 0, 1024, 1024));
        return control;
    }

    private static void ApplyInitial(BScanControl control) => control.ApplyRaster(
        Snapshot(1, 0, true, new(0, 0, 1024, 1024)), FillBgra);

    private static unsafe bool FillBgra(nint destination, int length, int stride)
    {
        new Span<byte>(destination.ToPointer(), length).Fill(128);
        return true;
    }

    private static BScanVisualSnapshot Snapshot(ulong revision, ulong basis, bool full,
        BScanRasterRectangle affected)
    {
        var run = new AcquisitionRunId(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var range = new BScanAcquisitionRange(0, 1023);
        var time = ScanTimeViewportOperations.Create(0, 1);
        return new(run, revision, basis, revision, 0, range, range, time,
            BScanSpatialViewportOperations.Reset(range), time, 1024, 1024, 1,
            new(0, 1023, basis, revision), affected, true, full,
            full ? BScanFullRefreshReason.Initial : BScanFullRefreshReason.None)
        { Stream = new(new UtSourceId("benchmark"), run, new PhysicalChannelId(0)) };
    }

    private sealed class StaHost : IDisposable
    {
        private readonly AutoResetEvent _requested = new(false);
        private readonly AutoResetEvent _completed = new(false);
        private readonly Thread _thread;
        private Action? _action;
        private ExceptionDispatchInfo? _failure;
        private bool _stopping;

        internal StaHost()
        {
            _thread = new(Run) { IsBackground = true, Name = "BScan WPF benchmark STA" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal void Invoke(Action action)
        {
            _action = action;
            _failure = null;
            _requested.Set();
            _completed.WaitOne();
            _failure?.Throw();
        }

        private void Run()
        {
            while (true)
            {
                _requested.WaitOne();
                if (_stopping) { _completed.Set(); return; }
                try { _action!(); }
                catch (Exception error) { _failure = ExceptionDispatchInfo.Capture(error); }
                finally { _completed.Set(); }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _requested.Set();
            _completed.WaitOne();
            _thread.Join();
            _requested.Dispose();
            _completed.Dispose();
        }
    }
}
