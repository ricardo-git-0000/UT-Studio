using UTStudio.Contracts.Inspection;
using UTStudio.Domain.Acquisition;
using UTStudio.Domain.Inspection;

namespace UTStudio.Inspection.Core;

/// <summary>Owns the bounded inspection-wide store and supplies its writer to successive runs.</summary>
public sealed class InspectionDataStoreRegistry : IAsyncDisposable
{
    private readonly InspectionId _inspectionId;
    private readonly InMemoryInspectionDataStore _store;

    public InspectionDataStoreRegistry(InspectionId inspectionId, InMemoryInspectionStoreOptions options)
    {
        if (!inspectionId.IsValid) { throw new ArgumentException("A valid inspection identifier is required.", nameof(inspectionId)); }
        ArgumentNullException.ThrowIfNull(options);
        _inspectionId = inspectionId;
        _store = new InMemoryInspectionDataStore(inspectionId, options);
    }

    public InspectionId InspectionId => _inspectionId;
    public IInspectionDataStore Store => _store;

    public IInspectionRunWriter OpenRun(AcquisitionRunId runId)
    {
        if (!runId.IsValid) { throw new ArgumentException("A valid run identifier is required.", nameof(runId)); }
        return _store.OpenRunAsync(runId).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
