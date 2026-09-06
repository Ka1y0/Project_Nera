namespace ChipsStudio.Nera.AgentBridge;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int ServiceUnavailable = 3;
    public const int PermissionDenied = 4;
    public const int RevisionConflict = 5;
    public const int RuntimeUnavailable = 6;
    public const int OperationFailed = 7;
    public const int TimeoutOrCancelled = 8;

    public static int FromStableCode(string? code) => code switch
    {
        NeraErrorCodes.ServiceUnavailable => ServiceUnavailable,
        NeraErrorCodes.InvalidArgument => Usage,
        NeraErrorCodes.PermissionDenied => PermissionDenied,
        NeraErrorCodes.RevisionConflict => RevisionConflict,
        NeraErrorCodes.RuntimeUnavailable => RuntimeUnavailable,
        NeraErrorCodes.ProtocolMismatch or
        NeraErrorCodes.MalformedResponse or
        NeraErrorCodes.ResponseTooLarge => OperationFailed,
        NeraErrorCodes.Timeout or NeraErrorCodes.Cancelled => TimeoutOrCancelled,
        _ => OperationFailed
    };
}

public static class NeraErrorCodes
{
    public const string InvalidArgument = "NERA_INVALID_ARGUMENT";
    public const string ServiceUnavailable = "NERA_SERVICE_UNAVAILABLE";
    public const string ProtocolMismatch = "NERA_PROTOCOL_MISMATCH";
    public const string PermissionDenied = "NERA_PERMISSION_DENIED";
    public const string RevisionConflict = "NERA_REVISION_CONFLICT";
    public const string RuntimeUnavailable = "NERA_RUNTIME_UNAVAILABLE";
    public const string Timeout = "NERA_TIMEOUT";
    public const string Cancelled = "NERA_CANCELLED";
    public const string NotFound = "NERA_NOT_FOUND";
    public const string OperationFailed = "NERA_OPERATION_FAILED";
    public const string MalformedResponse = "NERA_MALFORMED_RESPONSE";
    public const string ResponseTooLarge = "NERA_RESPONSE_TOO_LARGE";
}
