namespace PizzaShop.Core;

/// <summary>
/// Stores the customer's tip selection.
/// When Type is Percentage, Value is a whole number percentage (e.g. 15 = 15% of the bill).
/// </summary>
public record TipInfo(TipType Type = TipType.None, decimal Value = 0m)
{
    /// <summary>
    /// Maximum allowed percentage tip (1000%).
    /// </summary>
    public const decimal MaxPercentage = 1000m;

    /// <summary>
    /// Default no-tip value.
    /// </summary>
    public static TipInfo Zero => new TipInfo(TipType.None, 0m);
}
