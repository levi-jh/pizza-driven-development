namespace PizzaShop.Cli;

/// <summary>
/// Error response shape returned by the Order Service when a request is rejected.
/// </summary>
public record RemoteErrorDto(string Message, string? ErrorCode = null);
