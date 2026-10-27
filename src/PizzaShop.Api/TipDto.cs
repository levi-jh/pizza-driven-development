using PizzaShop.Core;

namespace PizzaShop.Api;

/// <summary>
/// Wire format for tip selection on an order submission or update.
/// </summary>
public record TipDto(TipType Type = TipType.None, decimal Value = 0m);
