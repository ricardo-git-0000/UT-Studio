namespace UTStudio.Domain.Acquisition;

/// <summary>A stable source-defined operational code and message, without platform exception types.</summary>
/// <remarks>Expected cancellation is not an operational error.</remarks>
public sealed class UtSourceError
{
    public UtSourceError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }
    public string Message { get; }
}
