using UTStudio.LoadTests;

namespace UTStudio.Core.Tests.Performance;

[TestClass]
public sealed class FunctionalCriteriaTests
{
    [TestMethod]
    public void VisualReplacementDoesNotFailBalancedAcquisition()
    {
        var result = Result(produced: 10, consumed: 10, released: 10, visualReceived: 10, published: 1, replaced: 9);
        Assert.IsEmpty(FunctionalCriteria.Evaluate(result));
        Assert.AreEqual(0, FunctionalCriteria.ExitCode(result));
    }

    [TestMethod]
    [DataRow(10, 9, 9, 0)]
    [DataRow(10, 10, 9, 0)]
    [DataRow(10, 10, 10, 1)]
    public void BalanceViolationsReturnFailureCode(long produced, long consumed, long released, int outstanding)
    {
        var result = Result(produced, consumed, released, outstanding: outstanding);
        Assert.IsNotEmpty(FunctionalCriteria.Evaluate(result));
        Assert.AreEqual(2, FunctionalCriteria.ExitCode(result));
    }

    private static LoadResult Result(long produced, long consumed, long released, int outstanding = 0,
        long visualReceived = 0, long published = 0, long replaced = 0) => new(
        produced, consumed, released, outstanding, 0, visualReceived, published, replaced,
        0, 0, 0, default, default, 0, 0, 0, 0, 0, 0, 0, 0, true, TimeSpan.Zero, [], false);
}
