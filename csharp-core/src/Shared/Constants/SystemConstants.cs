namespace NexusPort.Shared.Constants;

public static class SystemConstants
{
    public const string AppName = "NexusPort";
    public const string CorsPolicy = "NexusPortCorsPolicy";
    public const string DefaultDbSchema = "public";
}

public static class RoleConstants
{
    public const string SuperAdmin = "SuperAdmin";
    public const string PortManager = "PortManager";
    public const string BerthPlanner = "BerthPlanner";
    public const string YardPlanner = "YardPlanner";
    public const string GateOfficer = "GateOfficer";
    public const string Dispatcher = "Dispatcher";
    public const string Driver = "Driver";
    public const string Carrier = "Carrier";
}

public static class ErrorCodes
{
    public const string InternalServerError = "INTERNAL_SERVER_ERROR";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string Conflict = "CONFLICT";
}
