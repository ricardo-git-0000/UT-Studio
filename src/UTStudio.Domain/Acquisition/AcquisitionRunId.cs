namespace UTStudio.Domain.Acquisition;

/// <summary>Identifies one execution. The caller supplies the identifier; default is invalid.</summary>
public readonly record struct AcquisitionRunId
{
    public AcquisitionRunId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An execution identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString();
}
