namespace PizzaShop.Core;

/// <summary>
/// A pizza size option on the menu (e.g. Small = $5, Medium = $7, Large = $12).
/// Kept as data instead of an enum so new sizes don't require changing pricing code.
/// </summary>
public record SizeDef(string Code, string Name, decimal BasePrice);
