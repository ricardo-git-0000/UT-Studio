namespace UTStudio.Visualization.Core;

public readonly record struct BScanMinMax(short Minimum, short Maximum);

public enum BScanTraceResultKind
{
    Available,
    NotAvailable,
    Gap,
    OutOfRange,
    Closed,
    Evicted,
    Cancelled,
    Faulted
}
