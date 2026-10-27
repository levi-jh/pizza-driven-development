namespace PizzaShop.Core;

internal sealed class MenuSeedConfig
{
    public decimal TaxRate { get; set; } = MenuCatalog.DefaultTaxRate;
    public List<SizeDef>? Sizes { get; set; }
    public List<ToppingCategoryDef>? Categories { get; set; }
    public List<ToppingDef>? Toppings { get; set; }
    public List<PromoSeedItem>? Promos { get; set; }
}
