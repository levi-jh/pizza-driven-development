namespace PizzaShop.Api;

/// <summary>
/// Standard error payload returned on 400, 404, and 409 responses.
/// </summary>
public record ApiErrorResponse(string Message, string? ErrorCode = null);
