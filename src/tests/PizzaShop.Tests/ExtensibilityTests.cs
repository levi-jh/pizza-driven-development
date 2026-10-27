using PizzaShop.Core;
using Xunit;

namespace PizzaShop.Tests;

public class ExtensibilityTests
{
    [Fact]
    public void AddingNewSizeToppingCategoryToppingAndPromo_WorksWithoutTouchingPricingCalculator()
    {
        var catalog = MenuCatalog.CreateDefault();

        // 1. Add a new size ($16.00 Party size)
        catalog.AddSize("party", "Party Size", 16.00m);

        // 2. Add a new topping category (Gourmet @ 15% of base pizza price)
        catalog.AddToppingCategory("gourmet", "Gourmet", 0.15m);

        // 3. Add a new topping in that category (15% of $16.00 = $2.40)
        catalog.AddTopping("prosciutto", "Prosciutto", "gourmet");

        // 4. Add a new promo rule with its own threshold ($30) and conflict with ILOVEPIZZA
        catalog.AddPromoRule(new ThresholdFlatDiscountPromo("PARTY8", "$8 off orders over $30", 8.00m, 30.00m, new[] { "ILOVEPIZZA" }));

        // Build an order with 2x Party pizzas + prosciutto: 2 * ($16.00 + $2.40) = $36.80
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem
                {
                    SizeCode = "party",
                    Quantity = 2,
                    Toppings = new List<string> { "prosciutto" }
                }
            },
            PromoCodes = new List<string> { "PARTY8" }
        };

        var calculator = new PricingCalculator(catalog);
        var breakdown = calculator.Calculate(draft);

        Assert.Equal(36.80m, breakdown.SubtotalBeforeDiscount);
        Assert.Equal(8.00m, breakdown.DiscountAmount);
        Assert.Equal(28.80m, breakdown.TaxableSubtotal);

        // Verify conflict between PARTY8 and ILOVEPIZZA is enforced automatically
        var canCombine = calculator.TryValidatePromoAddition(draft, "ILOVEPIZZA", out _);
        Assert.False(canCombine);
    }

    [Fact]
    public async Task LoadFromJson_SeedsCustomSizesCategoriesToppingsAndPromosFromData()
    {
        var customJson = @"{
          ""taxRate"": 0.065,
          ""sizes"": [
            { ""code"": ""xl"", ""name"": ""Extra Large"", ""basePrice"": 15.00 }
          ],
          ""categories"": [
            { ""code"": ""artisan"", ""name"": ""Artisan"", ""basePricePct"": 0.12 }
          ],
          ""toppings"": [
            { ""code"": ""truffle"", ""name"": ""Truffle"", ""categoryCode"": ""artisan"" }
          ],
          ""promos"": [
            {
              ""code"": ""VIP20"",
              ""type"": ""percentage"",
              ""description"": ""20% off"",
              ""rate"": 0.20,
              ""conflictsWith"": [""FLAT10""]
            },
            {
              ""code"": ""FLAT10"",
              ""type"": ""thresholdFlat"",
              ""description"": ""$10 off over $25"",
              ""amount"": 10.00,
              ""minSubtotalExclusive"": 25.00,
              ""conflictsWith"": [""VIP20""]
            }
          ]
        }";

        var catalog = MenuCatalog.LoadFromJson(customJson);
        var calculator = new PricingCalculator(catalog);

        // 2x XL ($15.00) + truffle (12% of $15.00 = $1.80) -> $16.80/ea * 2 = $33.60
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem
                {
                    SizeCode = "xl",
                    Quantity = 2,
                    Toppings = new List<string> { "truffle" }
                }
            },
            PromoCodes = new List<string> { "FLAT10" }
        };

        var result = calculator.Calculate(draft);
        Assert.Equal(33.60m, result.SubtotalBeforeDiscount);
        Assert.Equal(10.00m, result.DiscountAmount);
        Assert.Equal(23.60m, result.TaxableSubtotal);

        // Verify default menu.json file on disk loads cleanly
        var fileCatalog = await MenuCatalog.LoadFromFileOrDefaultAsync();
        Assert.True(fileCatalog.TryGetSize("small", out _));
        Assert.True(fileCatalog.TryGetPromo("ILOVEPIZZA", out _));

        // Verify missing seed file path falls back to CreateDefault() without crashing
        var fallbackCatalog = await MenuCatalog.LoadFromFileOrDefaultAsync("nonexistent-seed-file.json");
        Assert.True(fallbackCatalog.TryGetSize("medium", out _));
    }

    [Fact]
    public async Task LoadFromFileOrDefaultAsync_WithMalformedJson_FallsBackToDefaultCatalog()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"malformed_menu_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, "{ this is not valid json! }");

            var catalog = await MenuCatalog.LoadFromFileOrDefaultAsync(tempFile);
            Assert.NotNull(catalog);
            Assert.True(catalog.TryGetSize("small", out var small));
            Assert.Equal(5.00m, small.BasePrice);
            Assert.True(catalog.TryGetPromo("SAVE5", out _));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
