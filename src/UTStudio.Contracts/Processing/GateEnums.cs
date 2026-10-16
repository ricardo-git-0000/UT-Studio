namespace UTStudio.Contracts.Processing;

public enum GateTimeReferenceKind { Unknown = 0, TraceOrigin = 1, GateEvent = 2 }
public enum GateSignalDomain { Unknown = 0, RawSigned = 1, Positive = 2, NegativeMagnitude = 3, Absolute = 4 }
public enum GateDetectorKind
{
    Unknown = 0, GlobalMaximum = 1, GlobalMinimum = 2, AbsoluteMaximum = 3,
    FirstRisingCrossing = 4, LastRisingCrossing = 5, FirstFallingCrossing = 6,
    LastFallingCrossing = 7, FirstLocalMaximum = 8, LastLocalMaximum = 9
}
public enum GatePlateauPolicy { Unknown = 0, LowerCenter = 1 }
public enum GateMeasureKind { Unknown = 0, Amplitude = 1, DetectionTime = 2, TimeOfFlight = 3, Status = 4, Quality = 5 }
public enum GateAcceptedTraceQuality { Unknown = 0, GoodOnly = 1, GoodAndDegraded = 2 }
public enum GateAmplitudeUnit { Unknown = 0, DigitalCounts = 1 }
public enum GateTimeBasis { Unknown = 0, TraceOrigin = 1 }
public enum GateTofReferenceKind { Unknown = 0, TraceOrigin = 1, DependencyGateEvent = 2 }
public enum GateResultStatus
{
    Unknown = 0, Detected = 1, BelowThreshold = 2, NoEvent = 3, DependencyNotDetected = 4,
    TraceNotAvailable = 5, WindowOutOfRange = 6, Cancelled = 7, Faulted = 8
}
public enum GatePlanCompilationStatus
{
    Unknown = 0, Compiled = 1, InvalidPlan = 2, DuplicateGate = 3, MissingDependency = 4,
    SelfDependency = 5, CycleDetected = 6, UnsupportedCombination = 7, CapacityExceeded = 8,
    Cancelled = 9, Faulted = 10
}
public enum GateCompilationErrorCode
{
    Unknown = 0, InvalidIdentity = 1, InvalidRevision = 2, InvalidVersion = 3, EmptyPlan = 4,
    CapacityExceeded = 5, DuplicateGate = 6, InvalidDuration = 7, TemporalOverflow = 8,
    InvalidThreshold = 9, InvalidHysteresis = 10, InvalidLocalExtremaParameters = 11,
    UnsupportedCombination = 12, MissingDependency = 13, SelfDependency = 14,
    CycleDetected = 15, InvalidMeasure = 16, InvalidTraceQuality = 17, Cancelled = 18,
    InternalFailure = 19
}
