namespace PizzaShop.Api;

/// <summary>
/// Wire format for a single pizza line item sent by the client.
/// </summary>
public record OrderLineItemDto(string SizeCode, int Quantity, List<string>? Toppings);
