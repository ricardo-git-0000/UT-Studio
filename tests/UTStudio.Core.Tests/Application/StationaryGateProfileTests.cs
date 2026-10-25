using UTStudio.Acquisition.Simulator;
using UTStudio.Application;
using UTStudio.Contracts.Processing;
using UTStudio.Core.Tests.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Core.Tests.Application;

[TestClass]
public sealed class StationaryGateProfileTests
{
    [TestMethod]
    public void AcceptedProfileCompilesAndEvaluatesAThenBAndCOnOriginalSamples()
    {
        var configuration = SimulatorUtFrameSource.DefaultConfiguration;
        CompiledGatePlan plan = ProductiveGateProcessingFactory.CompileStationaryAbc(configuration);
        GateDefinition[] gates = plan.Source.Gates.ToArray();

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, gates.Select(x => x.Id.Value).ToArray());
        Assert.AreEqual(GateTimeReferenceKind.TraceOrigin, gates[0].Window.StartReference.Kind);
        Assert.AreEqual(GateTimeReferenceKind.GateEvent, gates[1].Window.StartReference.Kind);
        Assert.AreEqual(gates[0].Id, gates[1].Window.StartReference.DependencyGateId);
        Assert.AreEqual(GateTimeReferenceKind.TraceOrigin, gates[2].Window.StartReference.Kind);
        Assert.IsTrue(gates.All(x => x.Detector.Kind == GateDetectorKind.GlobalMaximum &&
            x.SignalDomain == GateSignalDomain.RawSigned &&
            x.Detector.Threshold?.NormalizedValue == .6));

        var options = new SimulatorOptions(noiseAmplitude: 0, signalProfile: SimulatorSignalProfiles.StationaryV1);
        var samples = new short[configuration.SampleCount];
        SyntheticRfGenerator.Fill(samples, configuration, options, options.SignalProfile, 0, default);
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(configuration.SampleCount, configuration.SampleRateHz,
                configuration.FirstSampleOffsetSeconds), samples).Batch!;

        Assert.IsTrue(batch.Entries.All(x => x.Status == GateResultStatus.Detected));
        Assert.IsTrue(batch.Entries[0].DetectionTimeSeconds is >= 8.5e-6 and < 11.5e-6);
        Assert.IsTrue(batch.Entries[1].DetectionTimeSeconds is >= 23.5e-6 and < 26.5e-6);
        Assert.IsTrue(batch.Entries[2].DetectionTimeSeconds is >= 23.5e-6 and < 26.5e-6);
        Assert.AreEqual(gates[0].Id, batch.Entries[1].CausalGateId);
    }

    [TestMethod]
    public void UnavailableParentWindowPropagatesWithoutZeroOrStaleAnchor()
    {
        var configuration = SimulatorUtFrameSource.DefaultConfiguration;
        CompiledGatePlan plan = ProductiveGateProcessingFactory.CompileStationaryAbc(configuration);
        var samples = new short[400];
        GateEvaluationBatch batch = new GateEvaluator().Evaluate(plan,
            GateEvaluationTestData.Trace(samples.Length, configuration.SampleRateHz), samples).Batch!;

        Assert.AreEqual(GateResultStatus.WindowOutOfRange, batch.Entries[0].Status);
        Assert.AreEqual(GateResultStatus.DependencyNotDetected, batch.Entries[1].Status);
        Assert.IsNull(batch.Entries[1].DetectionTimeSeconds);
        Assert.AreEqual(new GateId("A"), batch.Entries[1].CausalGateId);
    }
}
