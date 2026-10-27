namespace PizzaShop.Core;

/// <summary>
/// Grouping for toppings that determines how much they cost relative to the pizza's base price.
/// Basic is 8% (0.08m), Premium is 11% (0.11m).
/// </summary>
public record ToppingCategoryDef(string Code, string Name, decimal BasePricePct);
