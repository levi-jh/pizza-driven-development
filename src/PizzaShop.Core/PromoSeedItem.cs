namespace PizzaShop.Core;

internal sealed class PromoSeedItem
{
    public string Code { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public decimal MinSubtotalExclusive { get; set; }
    public List<string>? ConflictsWith { get; set; }
}
