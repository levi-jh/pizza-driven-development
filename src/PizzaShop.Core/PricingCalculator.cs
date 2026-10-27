namespace PizzaShop.Core;

/// <summary>
/// Single source of truth for order pricing, taking a MenuCatalog via DI.
/// Shared by both the CLI (for instant local running totals) and the API server (for validation
/// and authoritative charging on submission and update).
/// </summary>
public class PricingCalculator
{
    public const decimal PercentToDecimalFactor = 100m;
    private const int CentsDecimalPlaces = 2;

    private readonly MenuCatalog _catalog;

    public PricingCalculator(MenuCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Computes the full itemized price breakdown for an OrderDraft.
    /// </summary>
    /// <returns>
    /// Complete itemized receipt calculation rounded to whole cents at each stage.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when draft is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the draft contains unknown menu codes, invalid quantities, negative tips, or mutually exclusive promo codes.
    /// </exception>
    public PricingBreakdown Calculate(OrderDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var lines = new List<LineItemBreakdown>(draft.Items.Count);
        var warnings = new List<string>();

        foreach (var item in draft.Items)
        {
            if (item.Quantity <= 0)
            {
                throw new ArgumentException($"Line item quantity must be at least 1 (got {item.Quantity}).");
            }

            if (!_catalog.TryGetSize(item.SizeCode, out var size))
            {
                throw new ArgumentException($"Unknown pizza size '{item.SizeCode}'.");
            }

            decimal toppingsUnitCost = 0m;
            var distinctToppings = new List<string>();

            foreach (var rawTopping in item.Toppings)
            {
                if (string.IsNullOrWhiteSpace(rawTopping))
                {
                    throw new ArgumentException("Topping code cannot be empty.");
                }

                // Deduplicate toppings defensively in case a caller bypassed OrderLineItem.AddTopping or sent duplicate entries in raw JSON.
                if (distinctToppings.Any(t => string.Equals(t, rawTopping.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!_catalog.TryGetTopping(rawTopping, out var toppingDef, out var categoryDef))
                {
                    throw new ArgumentException($"Unknown topping '{rawTopping}'.");
                }

                distinctToppings.Add(toppingDef.Code);

                // Round each topping's price to cents so unit math stays consistent if a future size or category rate produces fractional cents.
                var singleToppingCost = RoundCents(size.BasePrice * categoryDef.BasePricePct);
                toppingsUnitCost += singleToppingCost;
            }

            var unitPrice = size.BasePrice + toppingsUnitCost;
            var lineTotal = RoundCents(unitPrice * item.Quantity);

            lines.Add(new LineItemBreakdown(size.Code, size.Name, item.Quantity, size.BasePrice, toppingsUnitCost, unitPrice, lineTotal, distinctToppings));
        }

        var subtotalBeforeDiscount = lines.Sum(l => l.LineTotal);

        // Resolve promo codes and verify there are no conflicts (e.g. SAVE5 + ILOVEPIZZA).
        var resolvedRules = new List<IPromoRule>();
        foreach (var code in draft.PromoCodes)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (!_catalog.TryGetPromo(code, out var rule))
            {
                throw new ArgumentException($"Invalid promo code '{code}'.");
            }

            if (resolvedRules.Any(r => string.Equals(r.Code, rule.Code, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Check conflicts in both directions so only one side needs to declare it.
            var conflictingExisting = resolvedRules.FirstOrDefault(existing => existing.ConflictsWith.Contains(rule.Code, StringComparer.OrdinalIgnoreCase) || rule.ConflictsWith.Contains(existing.Code, StringComparer.OrdinalIgnoreCase));
            if (conflictingExisting != null)
            {
                throw new ArgumentException($"Promo code '{rule.Code}' cannot be combined with '{conflictingExisting.Code}'.");
            }

            resolvedRules.Add(rule);
        }

        decimal totalDiscount = 0m;
        var appliedPromos = new List<string>();

        foreach (var rule in resolvedRules)
        {
            // If a customer adds SAVE5 when their cart is $24 and then deletes a pizza so the cart drops to $12,
            // throwing here would crash the running total loop during item removal. Instead we drop the discount to $0 and surface a warning.
            if (!rule.IsEligible(draft, subtotalBeforeDiscount, out var reason))
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    warnings.Add(reason);
                }

                continue;
            }

            var ruleDiscount = RoundCents(rule.CalculateDiscount(draft, subtotalBeforeDiscount));
            if (ruleDiscount > 0m)
            {
                totalDiscount += ruleDiscount;
                appliedPromos.Add(rule.Code);
            }
        }

        // Never let a promo discount push the subtotal below $0.00.
        totalDiscount = Math.Min(subtotalBeforeDiscount, totalDiscount);
        var taxableSubtotal = subtotalBeforeDiscount - totalDiscount;

        // Promos apply to the total order before tax, so tax is calculated on the discounted subtotal.
        var taxAmount = RoundCents(taxableSubtotal * _catalog.TaxRate);
        var preTipTotal = taxableSubtotal + taxAmount;

        if (draft.Tip.Value < 0m)
        {
            throw new ArgumentException("Tip cannot be negative.");
        }

        if (draft.Tip.Type == TipType.Percentage && draft.Tip.Value > TipInfo.MaxPercentage)
        {
            throw new ArgumentException($"Tip percentage cannot exceed {TipInfo.MaxPercentage}%.");
        }

        // Percentage tips are calculated against preTipTotal (post-discount, post-tax bill).
        decimal tipAmount;
        switch (draft.Tip.Type)
        {
            case TipType.FixedDollars:
                tipAmount = RoundCents(draft.Tip.Value);
                break;
            case TipType.Percentage:
                tipAmount = RoundCents(preTipTotal * (draft.Tip.Value / PercentToDecimalFactor));
                break;
            case TipType.None:
            default:
                tipAmount = 0m;
                break;
        }

        var grandTotal = preTipTotal + tipAmount;

        return new PricingBreakdown(lines, subtotalBeforeDiscount, totalDiscount, taxableSubtotal, taxAmount, preTipTotal, tipAmount, grandTotal, appliedPromos, warnings);
    }

    /// <summary>
    /// Helper used by the CLI when the user types a promo code.
    /// Validates that the code exists, doesn't conflict with an active code, and meets minimum spend criteria before adding it.
    /// </summary>
    /// <returns>
    /// True if the promo code can be applied to the draft; false otherwise.
    /// </returns>
    public bool TryValidatePromoAddition(OrderDraft draft, string? candidateCode, out string message)
    {
        if (string.IsNullOrWhiteSpace(candidateCode))
        {
            message = "Promo code cannot be empty.";
            return false;
        }

        if (!_catalog.TryGetPromo(candidateCode, out var candidateRule))
        {
            message = $"Unknown promo code '{candidateCode.Trim()}'. Valid codes: {string.Join(", ", _catalog.Promos.Keys)}";
            return false;
        }

        if (draft.PromoCodes.Any(c => string.Equals(c, candidateRule.Code, StringComparison.OrdinalIgnoreCase)))
        {
            message = $"Promo code '{candidateRule.Code}' is already applied.";
            return false;
        }

        foreach (var existingCode in draft.PromoCodes)
        {
            if (!_catalog.TryGetPromo(existingCode, out var existingRule))
            {
                continue;
            }

            if (existingRule.ConflictsWith.Contains(candidateRule.Code, StringComparer.OrdinalIgnoreCase) || candidateRule.ConflictsWith.Contains(existingRule.Code, StringComparer.OrdinalIgnoreCase))
            {
                message = $"Promo code '{candidateRule.Code}' cannot be combined with '{existingRule.Code}'. Remove '{existingRule.Code}' first.";
                return false;
            }
        }

        var currentPricing = Calculate(draft);
        if (!candidateRule.IsEligible(draft, currentPricing.SubtotalBeforeDiscount, out var reason))
        {
            message = reason ?? $"Promo code '{candidateRule.Code}' is not eligible for the current order.";
            return false;
        }

        message = $"Applied promo code '{candidateRule.Code}' ({candidateRule.Description}).";
        return true;
    }

    /// <summary>
    /// Pure math helper that rounds a decimal dollar amount to 2 decimal places (whole cents).
    /// Explicitly uses MidpointRounding.AwayFromZero instead of .NET's default ToEven (banker's rounding) so $0.005 rounds up to $0.01.
    /// </summary>
    /// <returns>
    /// Dollar amount rounded to 2 decimal places.
    /// </returns>
    public static decimal RoundCents(decimal value)
    {
        return Math.Round(value, CentsDecimalPlaces, MidpointRounding.AwayFromZero);
    }
}
