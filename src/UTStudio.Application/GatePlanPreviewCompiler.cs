using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;
using System.Security.Cryptography;

namespace UTStudio.Application;

/// <summary>Neutral Preview adapter over the existing pure gate-plan compiler.</summary>
public sealed class GatePlanPreviewCompiler : IGatePlanPreviewCompiler, IGatePlanPreviewCandidateValidator
{
    private readonly GatePlanCompiler _compiler;
    private readonly byte[] _provenanceKey = RandomNumberGenerator.GetBytes(32);
    public GatePlanPreviewCompiler(GatePlanCompiler? compiler = null) => _compiler = compiler ?? new();

    public GatePlanPreviewCompilationResult Compile(GatePlanDraft draft, GatePreviewIdentity previewIdentity)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!previewIdentity.IsValid) throw new ArgumentException("A valid Preview identity is required.", nameof(previewIdentity));
        if (draft.BaseRevision.Value == uint.MaxValue)
            return new(GatePlanPreviewCompilationStatus.RevisionExhausted);
        var candidateRevision = new GatePlanRevision(draft.BaseRevision.Value + 1);
        var plan = new GatePlan(draft.Authority.PlanId, candidateRevision, draft.SchemaVersion,
            draft.AlgorithmVersion, draft.Gates);
        GatePlanCompilationResult compilation = _compiler.Compile(plan);
        if (!compilation.IsCompiled)
            return new(GatePlanPreviewCompilationStatus.ValidationFailed, errors: compilation.Errors);
        CompiledGatePlan compiled = compilation.CompiledPlan!;
        string proof = ComputeProof(draft, candidateRevision, previewIdentity, compiled.Signature);
        return new(GatePlanPreviewCompilationStatus.Compiled,
            new(draft, candidateRevision, previewIdentity, compiled, proof));
    }

    public bool IsAuthentic(GatePlanPreviewCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.BaseRevision.Value == uint.MaxValue ||
            candidate.CandidateRevision.Value != candidate.BaseRevision.Value + 1 ||
            candidate.Draft.BaseRevision != candidate.BaseRevision ||
            candidate.Draft.Authority.PlanId != candidate.CompiledPlan.Source.Id ||
            candidate.CompiledPlan.Source.Revision != candidate.CandidateRevision)
            return false;
        string expected = ComputeProof(candidate.Draft, candidate.CandidateRevision,
            candidate.PreviewIdentity, candidate.Signature);
        byte[] left; byte[] right;
        try { left = Convert.FromHexString(expected); right = Convert.FromHexString(candidate.ProvenanceProof); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private string ComputeProof(GatePlanDraft draft, GatePlanRevision candidateRevision,
        GatePreviewIdentity previewIdentity, GatePlanSignature signature)
    {
        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(draft.Authority.InspectionId.Value.ToByteArray());
            writer.Write(draft.Authority.PlanId.Value);
            writer.Write(draft.BaseRevision.Value); writer.Write(candidateRevision.Value);
            writer.Write(draft.SchemaVersion.Value); writer.Write(draft.AlgorithmVersion.Value);
            writer.Write(previewIdentity.Value.ToByteArray()); writer.Write(previewIdentity.Generation);
            writer.Write(signature.Bytes.Span);
            writer.Write(draft.Gates.Count);
            foreach (GateDefinition gate in draft.Gates) WriteGate(writer, gate);
        }
        return Convert.ToHexString(HMACSHA256.HashData(_provenanceKey, material.GetBuffer().AsSpan(0, checked((int)material.Length))));
    }

    private static void WriteGate(BinaryWriter writer, GateDefinition gate)
    {
        writer.Write(gate.Id.Value); writer.Write(gate.Revision.Value);
        writer.Write((int)gate.Window.StartReference.Kind);
        writer.Write(gate.Window.StartReference.DependencyGateId.Value);
        writer.Write(BitConverter.DoubleToInt64Bits(gate.Window.StartOffsetSeconds));
        writer.Write(BitConverter.DoubleToInt64Bits(gate.Window.DurationSeconds));
        writer.Write((int)gate.SignalDomain); writer.Write((int)gate.Detector.Kind);
        WriteOptional(writer, gate.Detector.Threshold?.NormalizedValue);
        writer.Write(gate.Detector.Hysteresis is not null);
        if (gate.Detector.Hysteresis is { } hysteresis)
        { writer.Write(BitConverter.DoubleToInt64Bits(hysteresis.LowNormalized)); writer.Write(BitConverter.DoubleToInt64Bits(hysteresis.HighNormalized)); }
        writer.Write(gate.Detector.LocalExtrema is not null);
        if (gate.Detector.LocalExtrema is { } local)
        {
            writer.Write(local.NeighborhoodRadiusSamples);
            writer.Write(BitConverter.DoubleToInt64Bits(local.MinimumProminenceCounts));
            writer.Write(local.MinimumDistanceSamples); writer.Write((int)local.PlateauPolicy);
        }
        writer.Write(gate.RequestedMeasures.Count);
        foreach (GateMeasureKind measure in gate.RequestedMeasures) writer.Write((int)measure);
    }

    private static void WriteOptional(BinaryWriter writer, double? value)
    { writer.Write(value.HasValue); if (value.HasValue) writer.Write(BitConverter.DoubleToInt64Bits(value.Value)); }
}
