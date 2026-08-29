namespace NexusPort.Shared.Exceptions;

public abstract class AppException : Exception
{
    public int StatusCode { get; }
    public string ErrorCode { get; }

    protected AppException(string message, int statusCode = 400, string errorCode = "BAD_REQUEST")
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

public class NotFoundException : AppException
{
    public NotFoundException(string resourceName, object key)
        : base($"{resourceName} with key '{key}' was not found.", 404, "NOT_FOUND")
    {
    }

    public NotFoundException(string message)
        : base(message, 404, "NOT_FOUND")
    {
    }
}

public class ValidationException : AppException
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation failures have occurred.", 422, "VALIDATION_ERROR")
    {
        Errors = errors;
    }

    public ValidationException(string propertyName, string errorMessage)
        : base(errorMessage, 422, "VALIDATION_ERROR")
    {
        Errors = new Dictionary<string, string[]>
        {
            { propertyName, new[] { errorMessage } }
        };
    }
}

public class UnauthorizedException : AppException
{
    public UnauthorizedException(string message = "Unauthorized access.")
        : base(message, 401, "UNAUTHORIZED")
    {
    }
}
