namespace UTStudio.Visualization.Core;

public readonly record struct BScanMinMax(short Minimum, short Maximum);

public enum BScanTraceResultKind
{
    Unknown = 0,
    Available = 1,
    NotAvailable = 2,
    Gap = 3,
    OutOfRange = 4,
    Closed = 5,
    Evicted = 6,
    Cancelled = 7,
    Faulted = 8
}
