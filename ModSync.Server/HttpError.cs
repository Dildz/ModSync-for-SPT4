namespace ModSync.Server;

/// <summary>
/// Custom exception we throw from route handlers to short-circuit with a specific HTTP status.
/// Caught at the top of <c>ModSyncHttpListener.Handle</c> and turned into an HTTP response.
///
/// `Exception` (vs ordinary class): inheriting from System.Exception lets `throw new HttpError(...)`
/// participate in normal try/catch flow, including stack traces and the call to <c>base(message)</c>
/// to set the exception's standard <c>Message</c> property.
/// </summary>
public class HttpError(int code, string message) : Exception(message)
{
    /// <summary>HTTP status code to send back to the client.</summary>
    public int Code { get; } = code;

    /// <summary>Standard HTTP reason-phrase for this status code.</summary>
    public string CodeMessage => Code switch
    {
        400 => "Bad Request",
        404 => "Not Found",
        _ => "Internal Server Error"
    };
}
