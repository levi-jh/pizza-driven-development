using PizzaShop.Core;
using Xunit;

namespace PizzaShop.Tests;

public class PricingTests
{
    private readonly MenuCatalog _catalog;
    private readonly PricingCalculator _calculator;
    private readonly OrderFormatter _formatter;

    public PricingTests()
    {
        _catalog = MenuCatalog.CreateDefault();
        _calculator = new PricingCalculator(_catalog);
        _formatter = new OrderFormatter(_catalog, _calculator);
    }

    [Theory]
    [InlineData("small", "pepperoni", 5.00, 0.40, 5.40)]      // 8% of $5 = $0.40
    [InlineData("small", "bacon", 5.00, 0.55, 5.55)]          // 11% of $5 = $0.55
    [InlineData("medium", "sausage", 7.00, 0.56, 7.56)]       // 8% of $7 = $0.56
    [InlineData("medium", "chicken", 7.00, 0.77, 7.77)]       // 11% of $7 = $0.77
    [InlineData("large", "onions", 12.00, 0.96, 12.96)]       // 8% of $12 = $0.96
    [InlineData("large", "pineapple", 12.00, 1.32, 13.32)]    // 11% of $12 = $1.32
    public void SingleTopping_CalculatesExactPercentageOfBasePrice(string size, string topping, decimal expectedBase, decimal expectedTopping, decimal expectedUnit)
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = size, Quantity = 1, Toppings = new List<string> { topping } }
            }
        };

        var result = _calculator.Calculate(draft);

        var line = Assert.Single(result.Lines);
        Assert.Equal(expectedBase, line.BasePrice);
        Assert.Equal(expectedTopping, line.ToppingsUnitCost);
        Assert.Equal(expectedUnit, line.UnitPrice);
        Assert.Equal(expectedUnit, result.SubtotalBeforeDiscount);
    }

    [Fact]
    public void ILovePizza_AppliesTenPercentPreTaxDiscount_AndTaxesDiscountedSubtotal()
    {
        // 1 small pizza ($5.00) + pepperoni ($0.40) = $5.40
        // ILOVEPIZZA (10%): -$0.54 -> Taxable = $4.86
        // Tax (6.5% of $4.86 = 0.3159 -> rounds AwayFromZero to $0.32)
        // Total = $5.18
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "small", Quantity = 1, Toppings = new List<string> { "pepperoni" } }
            },
            PromoCodes = new List<string> { "ILOVEPIZZA" }
        };

        var result = _calculator.Calculate(draft);

        Assert.Equal(5.40m, result.SubtotalBeforeDiscount);
        Assert.Equal(0.54m, result.DiscountAmount);
        Assert.Equal(4.86m, result.TaxableSubtotal);
        Assert.Equal(0.32m, result.TaxAmount);
        Assert.Equal(5.18m, result.GrandTotal);
    }

    [Fact]
    public void Save5_StrictOverTwentyDollarBoundary()
    {
        // 4 small plain pizzas = $20.00 flat. Rule states "over $20", so $20.00 does not qualify.
        var exactTwentyDraft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "small", Quantity = 4 }
            },
            PromoCodes = new List<string> { "SAVE5" }
        };

        var atTwenty = _calculator.Calculate(exactTwentyDraft);
        Assert.Equal(20.00m, atTwenty.SubtotalBeforeDiscount);
        Assert.Equal(0.00m, atTwenty.DiscountAmount);
        Assert.NotEmpty(atTwenty.Warnings);

        // Adding one basic topping ($0.40) brings subtotal to $21.60 (> $20.00), so SAVE5 applies.
        exactTwentyDraft.Items[0].AddTopping("pepperoni");
        var overTwenty = _calculator.Calculate(exactTwentyDraft);
        Assert.Equal(21.60m, overTwenty.SubtotalBeforeDiscount);
        Assert.Equal(5.00m, overTwenty.DiscountAmount);
        Assert.Equal(16.60m, overTwenty.TaxableSubtotal);
        Assert.Empty(overTwenty.Warnings);
    }

    [Fact]
    public void Save5_AutomaticallyDeactivatesWhenCartShrinksUnderThreshold()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "large", Quantity = 2 } // $24.00
            },
            PromoCodes = new List<string> { "SAVE5" }
        };

        var initial = _calculator.Calculate(draft);
        Assert.Equal(5.00m, initial.DiscountAmount);

        // Customer reduces quantity to 1 ($12.00)
        draft.Items[0].Quantity = 1;
        var updated = _calculator.Calculate(draft);

        Assert.Equal(12.00m, updated.SubtotalBeforeDiscount);
        Assert.Equal(0.00m, updated.DiscountAmount);
        Assert.Single(updated.Warnings);
    }

    [Fact]
    public void ConflictingPromos_AreRejectedInBothOrders()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "large", Quantity = 2 }
            },
            PromoCodes = new List<string> { "ILOVEPIZZA" }
        };

        var canAddSave5 = _calculator.TryValidatePromoAddition(draft, "SAVE5", out var msg1);
        Assert.False(canAddSave5);
        Assert.Contains("cannot be combined", msg1, StringComparison.OrdinalIgnoreCase);

        // Verify PricingCalculator.Calculate also throws if conflicting codes are forced into the list
        draft.PromoCodes.Add("SAVE5");
        Assert.Throws<ArgumentException>(() => _calculator.Calculate(draft));
    }

    [Fact]
    public void TipModes_SupportFixedDollarsAndPercentageOfBill()
    {
        // 1 Large plain pizza = $12.00. Tax (6.5%) = $0.78. Bill before tip = $12.78.
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "large", Quantity = 1 }
            },
            Tip = new TipInfo(TipType.FixedDollars, 3.25m)
        };

        var fixedResult = _calculator.Calculate(draft);
        Assert.Equal(12.78m, fixedResult.PreTipTotal);
        Assert.Equal(3.25m, fixedResult.TipAmount);
        Assert.Equal(16.03m, fixedResult.GrandTotal);

        // 15% tip on $12.78 bill = 1.917 -> $1.92
        draft.Tip = new TipInfo(TipType.Percentage, 15m);
        var pctResult = _calculator.Calculate(draft);
        Assert.Equal(1.92m, pctResult.TipAmount);
        Assert.Equal(14.70m, pctResult.GrandTotal);
    }

    [Theory]
    [InlineData("small", 1, new[] { "pepperoni", "bacon" }, "ILOVEPIZZA", TipType.Percentage, 18)]
    [InlineData("medium", 3, new[] { "sausage", "onions", "garlic" }, "SAVE5", TipType.FixedDollars, 4.50)]
    [InlineData("large", 2, new[] { "extra cheese", "pineapple", "artichoke hearts" }, "", TipType.Percentage, 12.5)]
    [InlineData("small", 5, new[] { "green peppers" }, "SAVE5", TipType.Percentage, 20)]
    public void ReceiptLines_AlwaysSumToExactGrandTotal(string size, int qty, string[] toppings, string promo, TipType tipType, decimal tipVal)
    {
        var promoList = new List<string>();
        if (!string.IsNullOrEmpty(promo))
        {
            promoList.Add(promo);
        }

        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = size, Quantity = qty, Toppings = toppings.ToList() }
            },
            PromoCodes = promoList,
            Tip = new TipInfo(tipType, tipVal)
        };

        var p = _calculator.Calculate(draft);

        // Every monetary value must be whole cents (at most 2 decimal places)
        Assert.Equal(p.SubtotalBeforeDiscount, Math.Round(p.SubtotalBeforeDiscount, 2));
        Assert.Equal(p.DiscountAmount, Math.Round(p.DiscountAmount, 2));
        Assert.Equal(p.TaxableSubtotal, Math.Round(p.TaxableSubtotal, 2));
        Assert.Equal(p.TaxAmount, Math.Round(p.TaxAmount, 2));
        Assert.Equal(p.TipAmount, Math.Round(p.TipAmount, 2));
        Assert.Equal(p.GrandTotal, Math.Round(p.GrandTotal, 2));

        // Displayed receipt lines must add up to the exact cent
        Assert.Equal(p.Lines.Sum(l => l.LineTotal), p.SubtotalBeforeDiscount);
        Assert.Equal(p.SubtotalBeforeDiscount - p.DiscountAmount, p.TaxableSubtotal);
        Assert.Equal(p.TaxableSubtotal + p.TaxAmount, p.PreTipTotal);
        Assert.Equal(p.PreTipTotal + p.TipAmount, p.GrandTotal);
    }

    [Fact]
    public void OrderFormatter_ProducesWrittenDescriptionWithQuantitySizeAndToppings()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "large", Quantity = 2, Toppings = new List<string> { "pepperoni", "bacon" } },
                new OrderLineItem { SizeCode = "small", Quantity = 1, Toppings = new List<string>() }
            }
        };

        var description = _formatter.DescribeOrder(draft);

        Assert.Contains("2x Large pizza with pepperoni, bacon", description);
        Assert.Contains("1x Small pizza plain (no toppings)", description);
    }

    [Fact]
    public void DuplicateToppingsOnSameLineItem_AreOnlyChargedOnce()
    {
        // Even if raw list input contains duplicate toppings with different casing or whitespace,
        // PricingCalculator deduplicates them so the customer isn't charged twice.
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem
                {
                    SizeCode = "small",
                    Quantity = 1,
                    Toppings = new List<string> { "pepperoni", "PEPPERONI", " pepperoni " }
                }
            }
        };

        var result = _calculator.Calculate(draft);
        var line = Assert.Single(result.Lines);

        Assert.Single(line.Toppings);
        Assert.Equal(0.40m, line.ToppingsUnitCost);
        Assert.Equal(5.40m, line.UnitPrice);
    }

    [Fact]
    public void CaseAndWhitespace_AreNormalizedAcrossSizesToppingsAndPromos()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem
                {
                    SizeCode = "  LaRgE ",
                    Quantity = 1,
                    Toppings = new List<string> { " Extra Cheese ", "SUN-DRIED TOMATOES" }
                }
            },
            PromoCodes = new List<string> { " ilovepizza " }
        };

        var result = _calculator.Calculate(draft);

        // Large ($12.00) + basic 8% ($0.96) + premium 11% ($1.32) = $14.28
        Assert.Equal(14.28m, result.SubtotalBeforeDiscount);
        Assert.Equal(1.43m, result.DiscountAmount); // 10% of 14.28 = 1.428 -> $1.43
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public void InvalidLineItemQuantity_ThrowsArgumentException(int invalidQty)
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "medium", Quantity = invalidQty }
            }
        };

        Assert.Throws<ArgumentException>(() => _calculator.Calculate(draft));
    }

    [Fact]
    public void NegativeTip_ThrowsArgumentException()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem> { new OrderLineItem { SizeCode = "small", Quantity = 1 } },
            Tip = new TipInfo(TipType.FixedDollars, -2.00m)
        };

        Assert.Throws<ArgumentException>(() => _calculator.Calculate(draft));
    }

    [Fact]
    public void UnknownSizeToppingOrPromo_ThrowsArgumentException()
    {
        var badSize = new OrderDraft
        {
            Items = new List<OrderLineItem> { new OrderLineItem { SizeCode = " colossal ", Quantity = 1 } }
        };
        Assert.Throws<ArgumentException>(() => _calculator.Calculate(badSize));

        var badTopping = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "small", Quantity = 1, Toppings = new List<string> { "anchovies" } }
            }
        };
        Assert.Throws<ArgumentException>(() => _calculator.Calculate(badTopping));

        var badPromo = new OrderDraft
        {
            Items = new List<OrderLineItem> { new OrderLineItem { SizeCode = "small", Quantity = 1 } },
            PromoCodes = new List<string> { "FREEPIZZA99" }
        };
        Assert.Throws<ArgumentException>(() => _calculator.Calculate(badPromo));
    }

    [Fact]
    public void OrderLineItem_AddAndRemoveTopping_HandlesCaseInsensitivity()
    {
        var item = new OrderLineItem { SizeCode = "medium", Quantity = 1 };

        Assert.True(item.AddTopping("Pepperoni"));
        Assert.False(item.AddTopping("PEPPERONI")); // already present
        Assert.Single(item.Toppings);

        Assert.True(item.RemoveTopping("pepperoni"));
        Assert.False(item.RemoveTopping("pepperoni")); // already removed
        Assert.Empty(item.Toppings);
    }

    [Fact]
    public void OrderDraft_CloneAndHasSameContentAs_DetectsLineItemPromoAndTipChanges()
    {
        var original = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem
                {
                    SizeCode = "large",
                    Quantity = 2,
                    Toppings = new List<string> { "pepperoni", "bacon" }
                }
            },
            PromoCodes = new List<string> { "SAVE5" },
            Tip = new TipInfo(TipType.Percentage, 15m)
        };

        var snapshot = original.Clone();
        Assert.True(original.HasSameContentAs(snapshot));

        // Modifying quantity, toppings, promo codes, or tip on the working draft is detected against the snapshot
        original.Items[0].Quantity = 3;
        Assert.False(original.HasSameContentAs(snapshot));

        original.Items[0].Quantity = 2;
        Assert.True(original.HasSameContentAs(snapshot));

        original.Items[0].AddTopping("onions");
        Assert.False(original.HasSameContentAs(snapshot));

        original.Items[0].RemoveTopping("onions");
        original.Tip = new TipInfo(TipType.FixedDollars, 5.00m);
        Assert.False(original.HasSameContentAs(snapshot));

        original.Tip = snapshot.Tip;
        Assert.True(original.HasSameContentAs(snapshot));

        original.PromoCodes.Clear();
        Assert.False(original.HasSameContentAs(snapshot));
    }

    [Fact]
    public void RoundCents_ExplicitlyUsesAwayFromZero_NotBankersRounding()
    {
        // In .NET, Math.Round(value, 2) defaults to MidpointRounding.ToEven (Banker's rounding).
        // Under ToEven: 0.025m -> 0.02m, 0.045m -> 0.04m, 0.065m -> 0.06m, 0.085m -> 0.08m.
        // AwayFromZero guarantees retail receipt expectations: always round 0.5 cents up.
        Assert.Equal(0.03m, PricingCalculator.RoundCents(0.025m));
        Assert.Equal(0.04m, PricingCalculator.RoundCents(0.035m));
        Assert.Equal(0.05m, PricingCalculator.RoundCents(0.045m));
        Assert.Equal(0.07m, PricingCalculator.RoundCents(0.065m));
        Assert.Equal(0.09m, PricingCalculator.RoundCents(0.085m));
    }

    [Fact]
    public void DuplicatePromoCodesInDraft_OnlyDiscountOnce()
    {
        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "large", Quantity = 2 } // $24.00
            },
            PromoCodes = new List<string> { "SAVE5", "save5", " SAVE5 " }
        };

        var result = _calculator.Calculate(draft);
        Assert.Equal(24.00m, result.SubtotalBeforeDiscount);
        Assert.Equal(5.00m, result.DiscountAmount);
        Assert.Single(result.AppliedPromos);
    }

    [Fact]
    public void DiscountAmount_IsCappedAtSubtotalBeforeDiscount_NeverNegativeSubtotal()
    {
        var customCatalog = MenuCatalog.CreateDefault();
        customCatalog.AddPromoRule(new ThresholdFlatDiscountPromo("BIG50", "$50 off", 50.00m, 0.00m));
        var customCalc = new PricingCalculator(customCatalog);

        var draft = new OrderDraft
        {
            Items = new List<OrderLineItem>
            {
                new OrderLineItem { SizeCode = "small", Quantity = 1 } // $5.00
            },
            PromoCodes = new List<string> { "BIG50" }
        };

        var result = customCalc.Calculate(draft);
        Assert.Equal(5.00m, result.SubtotalBeforeDiscount);
        Assert.Equal(5.00m, result.DiscountAmount);
        Assert.Equal(0.00m, result.TaxableSubtotal);
        Assert.Equal(0.00m, result.TaxAmount);
        Assert.Equal(0.00m, result.GrandTotal);
    }
}
