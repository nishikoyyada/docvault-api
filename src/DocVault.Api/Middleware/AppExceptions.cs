namespace DocVault.Api.Middleware;

public class AppException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string message) : AppException(message, StatusCodes.Status404NotFound);

public sealed class BadRequestException(string message) : AppException(message, StatusCodes.Status400BadRequest);

public sealed class ConflictException(string message) : AppException(message, StatusCodes.Status409Conflict);
