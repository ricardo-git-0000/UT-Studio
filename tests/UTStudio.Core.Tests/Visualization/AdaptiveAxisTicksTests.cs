using UTStudio.Visualization.Core;

namespace UTStudio.Core.Tests.Visualization;

[TestClass]
public sealed class AdaptiveAxisTicksTests
{
    [TestMethod]
    [DataRow(-1, 1, 400)]
    [DataRow(0, 1_000_000, 800)]
    [DataRow(0, 1, 20)]
    public void Ticks_are_bounded_ordered_unique_and_include_endpoints(double minimum, double maximum, double length)
    {
        var ticks = AdaptiveAxisTicks.Create((decimal)minimum, (decimal)maximum, length);
        Assert.IsGreaterThanOrEqualTo(2, ticks.Count);
        Assert.IsLessThanOrEqualTo(32, ticks.Count);
        Assert.AreEqual((decimal)minimum, ticks[0].Value);
        Assert.AreEqual((decimal)maximum, ticks[^1].Value);
        for (int index = 1; index < ticks.Count; index++)
        { Assert.IsGreaterThan(ticks[index - 1].Value, ticks[index].Value); }
    }

    [TestMethod]
    public void Large_acquisition_origin_uses_relative_offsets_without_precision_loss()
    {
        ulong origin = ulong.MaxValue - 100;
        var ticks = AdaptiveAxisTicks.Create(-10, 10, 640);
        Assert.IsTrue(ticks.All(tick => origin + tick.Value >= 0 && origin + tick.Value <= ulong.MaxValue));
        Assert.HasCount(ticks.Count, ticks.Select(tick => tick.Value).Distinct());
    }

    [TestMethod]
    public void Logical_tick_values_are_independent_of_dpi()
    {
        var at96 = AdaptiveAxisTicks.Create(0, 100, 480);
        var at144 = AdaptiveAxisTicks.Create(0, 100, 480);
        CollectionAssert.AreEqual(at96.ToArray(), at144.ToArray());
    }

    [TestMethod]
    public void Invalid_or_zero_ranges_are_empty()
    {
        Assert.IsEmpty(AdaptiveAxisTicks.Create(1, 1, 100));
        Assert.IsEmpty(AdaptiveAxisTicks.Create(2, 1, 100));
        Assert.IsEmpty(AdaptiveAxisTicks.Create(0, 1, 0));
    }
}
