namespace PizzaShop.Core;

/// <summary>
/// Takes a flat dollar amount off the pre-tax subtotal once the subtotal exceeds a threshold
/// (used for SAVE5 = $5 off orders strictly over $20 before tax).
/// </summary>
public class ThresholdFlatDiscountPromo : IPromoRule
{
    private readonly HashSet<string> _conflicts;

    /// <summary>
    /// Uppercase promo code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable description of the discount.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Dollar discount taken off when the threshold is met.
    /// </summary>
    public decimal DiscountAmount { get; }

    /// <summary>
    /// Minimum pre-tax subtotal required (exclusive). For SAVE5 this is 20.00m ("over $20").
    /// </summary>
    public decimal MinSubtotalExclusive { get; }

    /// <summary>
    /// Promo codes that cannot be combined with this rule.
    /// </summary>
    public IReadOnlyCollection<string> ConflictsWith => _conflicts;

    public ThresholdFlatDiscountPromo(string code, string description, decimal discountAmount, decimal minSubtotalExclusive, IEnumerable<string>? conflictsWith = null)
    {
        Code = code.Trim().ToUpperInvariant();
        Description = description;
        DiscountAmount = discountAmount;
        MinSubtotalExclusive = minSubtotalExclusive;
        _conflicts = new HashSet<string>(conflictsWith?.Select(c => c.Trim().ToUpperInvariant()) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the pre-tax subtotal is strictly greater than MinSubtotalExclusive.
    /// </summary>
    /// <returns>
    /// True if the pre-tax subtotal exceeds the minimum threshold; false otherwise.
    /// </returns>
    public bool IsEligible(OrderDraft draft, decimal subtotalBeforeDiscount, out string? ineligibleReason)
    {
        // Spec says "over $20", so $20.00 flat does not qualify and needs to be at least $20.01.
        if (subtotalBeforeDiscount <= MinSubtotalExclusive)
        {
            ineligibleReason = $"{Code} requires an order total before tax over ${MinSubtotalExclusive:0.00} (currently ${subtotalBeforeDiscount:0.00}).";
            return false;
        }

        ineligibleReason = null;
        return true;
    }

    /// <summary>
    /// Computes the flat dollar discount when the subtotal exceeds the minimum threshold.
    /// </summary>
    /// <returns>
    /// DiscountAmount when eligible, or 0m when below the threshold.
    /// </returns>
    public decimal CalculateDiscount(OrderDraft draft, decimal subtotalBeforeDiscount)
    {
        if (subtotalBeforeDiscount <= MinSubtotalExclusive)
        {
            return 0m;
        }

        return DiscountAmount;
    }
}
