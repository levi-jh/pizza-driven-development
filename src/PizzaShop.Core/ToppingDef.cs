namespace PizzaShop.Core;

/// <summary>
/// Individual topping item linked to a ToppingCategoryDef by code.
/// </summary>
public record ToppingDef(string Code, string Name, string CategoryCode);
