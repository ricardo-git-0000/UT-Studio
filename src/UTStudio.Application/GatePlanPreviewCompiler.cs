using UTStudio.Contracts.Processing;
using UTStudio.Domain.Processing;
using UTStudio.Processing.Core;

namespace UTStudio.Application;

/// <summary>Neutral Preview adapter over the existing pure gate-plan compiler.</summary>
public sealed class GatePlanPreviewCompiler : IGatePlanPreviewCompiler
{
    private readonly GatePlanCompiler _compiler;
    public GatePlanPreviewCompiler(GatePlanCompiler? compiler = null) => _compiler = compiler ?? new();

    public GatePlanCompilationResult Compile(GatePlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var plan = new GatePlan(draft.Authority.PlanId, draft.BaseRevision, draft.SchemaVersion,
            draft.AlgorithmVersion, draft.Gates);
        return _compiler.Compile(plan);
    }
}
