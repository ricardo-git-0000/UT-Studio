using System.Windows.Media.Imaging;
using System.Windows;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanControlTests
{
    [TestMethod]
    public void Palette_is_deterministic_and_distinguishes_reserved_and_rf_anchors()
    {
        var colors = Enumerable.Range(0, 256).Select(i => BScanPalette.GetColor((byte)i)).ToArray();
        CollectionAssert.AreEqual(colors, Enumerable.Range(0, 256).Select(i => BScanPalette.GetColor((byte)i)).ToArray());
        Assert.AreNotEqual(colors[0], colors[1]);
        Assert.IsGreaterThan(colors[1].R, colors[1].B);
        Assert.IsGreaterThan(colors[255].B, colors[255].R);
        Assert.AreEqual(colors[128].R, colors[128].G, 10);
    }

    [TestMethod]
    public Task Full_and_incremental_updates_reuse_bitmap_and_preserve_unaffected_pixels() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        var full = Snapshot(1, 0, 4, true, new(0, 0, 4, 2));
        Assert.IsTrue(control.ApplyRaster(full, (address, length, stride) => { Fill(Surface(address, length), stride, 4, 2, row => row == 0 ? (byte)1 : (byte)255); return true; }));
        var bitmap = (WriteableBitmap)control.RasterSource!;
        byte[] before = Pixels(bitmap);
        var incremental = Snapshot(2, 1, 2, false, new(1, 0, 1, 2));
        Assert.IsTrue(control.ApplyRaster(incremental, (address, length, stride) => { Span<byte> target = Surface(address, length); Set(target, stride, 0, 0, 128); Set(target, stride, 0, 1, 128); return true; }));
        Assert.AreSame(bitmap, control.RasterSource);
        byte[] after = Pixels(bitmap);
        Assert.AreEqual(before[0], after[0]);
        Assert.AreNotEqual(before[4], after[4]);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Incremental_with_wrong_base_is_rejected_and_requests_full_refresh() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        int requests = 0;
        control.FullRefreshRequired += (_, _) => requests++;
        Assert.IsTrue(control.ApplyRaster(Snapshot(1, 0, 4, true, new(0, 0, 4, 2)), (address, length, stride) => { Fill(Surface(address, length), stride, 4, 2, _ => 1); return true; }));
        Assert.IsFalse(control.ApplyRaster(Snapshot(3, 99, 3, false, new(0, 0, 1, 2)), (_, _, _) => true));
        Assert.AreEqual(1, requests);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Narrow_control_renders_offscreen_at_96_and_144_dpi_without_children() => StaTest.Run(() =>
    {
        foreach (double dpi in new[] { 96d, 144d })
        {
            var control = new BScanControl();
            control.Measure(new Size(72, 48));
            control.Arrange(new Rect(0, 0, 72, 48));
            control.UpdateLayout();
            var target = new RenderTargetBitmap((int)(72 * dpi / 96), (int)(48 * dpi / 96), dpi, dpi,
                System.Windows.Media.PixelFormats.Pbgra32);
            target.Render(control);
            Assert.AreEqual(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(control));
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Failed_or_throwing_copy_preserves_surface_identity_and_pixels_then_recovers() => StaTest.Run(() =>
    {
        var control = new BScanControl(); int fullRequests = 0;
        control.FullRefreshRequired += (_, _) => fullRequests++;
        BScanVisualSnapshot full = Snapshot(1, 0, 1, true, new(0, 0, 4, 2));
        Assert.IsTrue(control.ApplyRaster(full, (address, length, stride) =>
        { Fill(Surface(address, length), stride, 4, 2, _ => 1); return true; }));
        var visible = control.RasterSource;
        byte[] original = Pixels((WriteableBitmap)visible!);
        BScanVisualSnapshot incremental = Snapshot(2, 1, 2, false, new(0, 0, 1, 2));
        Assert.IsFalse(control.ApplyRaster(incremental, (address, length, _) =>
        { Surface(address, length)[0] = 255; return false; }));
        Assert.AreSame(visible, control.RasterSource);
        CollectionAssert.AreEqual(original, Pixels((WriteableBitmap)control.RasterSource!));
        Assert.AreEqual(1UL, control.AppliedSnapshot!.StoreRevision);
        Assert.AreEqual(1, fullRequests);
        Assert.ThrowsExactly<InvalidOperationException>(() => control.ApplyRaster(
            incremental with { SnapshotVersion = 4 },
            (_, _, _) => throw new InvalidOperationException("copy")));
        Assert.AreSame(visible, control.RasterSource);
        CollectionAssert.AreEqual(original, Pixels((WriteableBitmap)control.RasterSource!));
        Assert.AreEqual(2, fullRequests);
        Assert.AreEqual(0, control.ActivePooledRasterBytes);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Incremental_commit_failure_on_every_row_rolls_back_exact_bytes_and_identity() => StaTest.Run(() =>
    {
        for (int failingRow = 0; failingRow < 2; failingRow++)
        {
            var control = new BScanControl();
            Assert.IsTrue(control.ApplyRaster(Snapshot(1, 0, 4, true, new(0, 0, 4, 2)),
                (address, length, stride) => { Fill(Surface(address, length), stride, 4, 2, row => (byte)(row + 1)); return true; }));
            var bitmap = (WriteableBitmap)control.RasterSource!;
            byte[] original = Pixels(bitmap);
            control.FailIncrementalCommitAtRow = failingRow;
            Assert.ThrowsExactly<InvalidOperationException>(() => control.ApplyRaster(
                Snapshot(2, 1, 5, false, new(1, 0, 2, 2)),
                (address, length, stride) => { Fill(Surface(address, length), stride, 2, 2, _ => 255); return true; }));
            Assert.AreSame(bitmap, control.RasterSource);
            CollectionAssert.AreEqual(original, Pixels(bitmap));
            Assert.AreEqual(1UL, control.AppliedSnapshot!.StoreRevision);
            Assert.AreEqual(0, control.ActivePooledRasterBytes);
        }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Failed_full_candidate_preserves_same_visible_bitmap_and_pixels() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        Assert.IsTrue(control.ApplyRaster(Snapshot(1, 0, 1, true, new(0, 0, 4, 2)),
            (address, length, stride) => { Fill(Surface(address, length), stride, 4, 2, _ => 1); return true; }));
        var visible = (WriteableBitmap)control.RasterSource!;
        byte[] original = Pixels(visible);
        control.FailFullCommitAtRow = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => control.ApplyRaster(
            Snapshot(2, 0, 2, true, new(0, 0, 4, 2)),
            (address, length, stride) => { Fill(Surface(address, length), stride, 4, 2, _ => 255); return true; }));
        Assert.AreSame(visible, control.RasterSource);
        CollectionAssert.AreEqual(original, Pixels(visible));
        Assert.AreEqual(1UL, control.AppliedSnapshot!.StoreRevision);
        Assert.AreEqual(0, control.ActivePooledRasterBytes);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Reentrant_clear_or_newer_apply_prevents_stale_commit() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        BScanVisualSnapshot first = Snapshot(1, 0, 1, true, new(0, 0, 4, 2));
        Assert.IsFalse(control.ApplyRaster(first, (address, length, stride) =>
        {
            Fill(Surface(address, length), stride, 4, 2, _ => 1);
            control.ClearRaster();
            return true;
        }));
        Assert.IsNull(control.RasterSource);

        BScanVisualSnapshot newer = Snapshot(3, 0, 3, true, new(0, 0, 4, 2));
        Assert.IsFalse(control.ApplyRaster(first, (address, length, stride) =>
        {
            Fill(Surface(address, length), stride, 4, 2, _ => 1);
            Assert.IsTrue(control.ApplyRaster(newer,
                (nested, nestedLength, nestedStride) => { Fill(Surface(nested, nestedLength), nestedStride, 4, 2, _ => 255); return true; }));
            return true;
        }));
        Assert.AreEqual(3UL, control.AppliedSnapshot!.StoreRevision);
        Assert.AreEqual(0, control.ActivePooledRasterBytes);
        return Task.CompletedTask;
    });

    private static byte[] Pixels(WriteableBitmap bitmap)
    { var bytes = new byte[bitmap.BackBufferStride * bitmap.PixelHeight]; bitmap.CopyPixels(bytes, bitmap.BackBufferStride, 0); return bytes; }

    private static void Fill(Span<byte> target, int stride, int width, int height, Func<int, byte> intensity)
    { for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) Set(target, stride, x, y, intensity(y)); }
    private static void Set(Span<byte> target, int stride, int x, int y, byte intensity)
    {
        uint color = BScanPalette.Bgra32[intensity]; int offset = y * stride + x * 4;
        target[offset] = (byte)color; target[offset + 1] = (byte)(color >> 8);
        target[offset + 2] = (byte)(color >> 16); target[offset + 3] = (byte)(color >> 24);
    }
    private static unsafe Span<byte> Surface(nint address, int length) => new(address.ToPointer(), length);

    private static BScanVisualSnapshot Snapshot(ulong revision, ulong baseRevision, ulong version, bool full,
        BScanRasterRectangle affected)
    {
        var run = new AcquisitionRunId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var range = new BScanAcquisitionRange(0, 3);
        var time = ScanTimeViewportOperations.Create(0, 1);
        return new(run, version, baseRevision, revision, 0, range, range, time,
            BScanSpatialViewportOperations.Reset(range), time, 4, 2, 1,
            new(0, 3, baseRevision, revision), affected, false, full,
            full ? BScanFullRefreshReason.Initial : BScanFullRefreshReason.None)
        { Stream = new(new UtSourceId("test"), run, new PhysicalChannelId(0)) };
    }
}
