using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Text;
using System.Xml;
using UTStudio.Domain.Acquisition;
using UTStudio.Visualization.Core;
using UTStudio.Visualization.Wpf.Controls;
using UTStudio.Visualization.Wpf.Rendering;

namespace UTStudio.Tests.Wpf;

[TestClass]
public sealed class BScanControlTests
{
    [TestMethod]
    public Task Selection_overlay_is_an_independent_visual_and_does_not_replace_the_raster() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        var window = new Window
        {
            Content = control,
            Width = 320,
            Height = 180,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        window.UpdateLayout();
        BScanVisualSnapshot full = Snapshot(1, 0, 1, true, new(0, 0, 4, 2));
        Assert.IsTrue(control.ApplyRaster(full, (address, length, stride) =>
        { Fill(Surface(address, length), stride, 4, 2, _ => 1); return true; }));
        control.SpatialViewport = full.SpatialViewport;
        var raster = control.RasterSource;

        control.SelectedAcquisitionIndex = 2;

        Assert.IsTrue(control.HasSelectionVisual);
        Assert.AreEqual(1, System.Windows.Media.VisualTreeHelper.GetChildrenCount(control));
        Assert.AreSame(raster, control.RasterSource);
        window.Close();
        return Task.CompletedTask;
    });

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
    public void Xml_palette_is_validated_and_interpolated_to_the_complete_intensity_domain()
    {
        const string xml = """
            <palette type="normal">
              <allColors>
                <color red="10" green="20" blue="30" />
                <color red="110" green="120" blue="130" />
              </allColors>
              <allMinColors><color red="1" green="2" blue="3" /></allMinColors>
              <allMaxColors><color red="250" green="251" blue="252" /></allMaxColors>
            </palette>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        BScanColorPalette palette = BScanPaletteFile.Load(stream, "fixture.xml");

        Assert.AreEqual("fixture.xml", palette.Name);
        Assert.AreEqual(256, palette.Bgra32.Length);
        Assert.AreEqual(0xFF010203u, palette.Bgra32.Span[0]);
        Assert.AreEqual(0xFF0A141Eu, palette.Bgra32.Span[1]);
        Assert.AreEqual(0xFFFAFBFCu, palette.Bgra32.Span[255]);
        Assert.AreNotEqual(palette.Bgra32.Span[1], palette.Bgra32.Span[128]);

        byte[] source = [0, 1, 255];
        byte[] destination = new byte[source.Length * 4];
        BScanPalette.CopyToBgra32(source, source.Length, destination, destination.Length,
            new(0, 0, source.Length, 1), palette.Bgra32.Span);

        CollectionAssert.AreEqual(new byte[]
        {
            3, 2, 1, 255,
            30, 20, 10, 255,
            252, 251, 250, 255
        }, destination);
    }

    [TestMethod]
    public void Xml_palette_rejects_dtd_and_invalid_channels()
    {
        using var dtd = new MemoryStream(Encoding.UTF8.GetBytes(
            "<!DOCTYPE palette [<!ENTITY x '1'>]><palette><allColors><color red='0' green='0' blue='0'/><color red='1' green='1' blue='1'/></allColors></palette>"));
        Assert.ThrowsExactly<XmlException>(() => BScanPaletteFile.Load(dtd));

        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes(
            "<palette><allColors><color red='999' green='0' blue='0'/><color red='1' green='1' blue='1'/></allColors></palette>"));
        Assert.ThrowsExactly<InvalidDataException>(() => BScanPaletteFile.Load(invalid));
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
    public Task Live_scroll_moves_existing_pixels_and_applies_only_new_right_columns() => StaTest.Run(() =>
    {
        var control = new BScanControl();
        BScanVisualSnapshot full = Snapshot(1, 0, 1, true, new(0, 0, 4, 2));
        Assert.IsTrue(control.ApplyRaster(full, (address, length, stride) =>
        {
            Span<byte> target = Surface(address, length);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++) { Set(target, stride, x, y, (byte)(10 + x)); }
            return true;
        }));
        var bitmap = (WriteableBitmap)control.RasterSource!;
        byte[] before = Pixels(bitmap);
        BScanVisualSnapshot shifted = full with
        {
            SnapshotVersion = 2,
            BaseStoreRevision = 1,
            StoreRevision = 2,
            WrapGeneration = 1,
            SpatialViewport = new(1, 0, 4),
            DirtyRange = new(4, 4, 1, 2),
            AffectedRegion = new(3, 0, 1, 2),
            RequiresFullRefresh = false,
            FullRefreshReason = BScanFullRefreshReason.None,
            HorizontalPixelShift = 1
        };

        Assert.IsTrue(control.ApplyRaster(shifted, (address, length, stride) =>
        {
            Span<byte> target = Surface(address, length);
            Set(target, stride, 0, 0, 20);
            Set(target, stride, 0, 1, 20);
            return true;
        }));

        byte[] after = Pixels(bitmap);
        for (int y = 0; y < 2; y++)
        {
            int row = y * bitmap.BackBufferStride;
            CollectionAssert.AreEqual(before[(row + 4)..(row + 16)], after[row..(row + 12)]);
        }
        control.FailIncrementalCommitAtRow = 0;
        BScanVisualSnapshot failing = shifted with
        {
            SnapshotVersion = 3,
            BaseStoreRevision = 2,
            StoreRevision = 3,
            SpatialViewport = new(2, 0, 4),
            DirtyRange = new(5, 5, 2, 3)
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => control.ApplyRaster(failing,
            (address, length, stride) =>
            {
                Span<byte> target = Surface(address, length);
                Set(target, stride, 0, 0, 30);
                Set(target, stride, 0, 1, 30);
                return true;
            }));
        CollectionAssert.AreEqual(after, Pixels(bitmap));
        control.FailIncrementalCommitAtRow = null;
        Assert.AreSame(bitmap, control.RasterSource);
        Assert.AreEqual(0, control.ActivePooledRasterBytes);
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
            Assert.AreEqual(1, System.Windows.Media.VisualTreeHelper.GetChildrenCount(control));
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
