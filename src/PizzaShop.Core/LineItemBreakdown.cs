namespace PizzaShop.Core;

/// <summary>
/// Calculated pricing details for a single pizza line item.
/// </summary>
public record LineItemBreakdown(string SizeCode, string SizeName, int Quantity, decimal BasePrice, decimal ToppingsUnitCost, decimal UnitPrice, decimal LineTotal, IReadOnlyList<string> Toppings);
