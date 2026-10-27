namespace PizzaShop.Core;

/// <summary>
/// Takes a percentage off the pre-tax subtotal (used for ILOVEPIZZA = 10% off).
/// </summary>
public class PercentageDiscountPromo : IPromoRule
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
    /// Fractional discount rate between 0 and 1 (e.g. 0.10m for 10%).
    /// </summary>
    public decimal DiscountRate { get; }

    /// <summary>
    /// Promo codes that cannot be combined with this rule.
    /// </summary>
    public IReadOnlyCollection<string> ConflictsWith => _conflicts;

    public PercentageDiscountPromo(string code, string description, decimal discountRate, IEnumerable<string>? conflictsWith = null)
    {
        Code = code.Trim().ToUpperInvariant();
        Description = description;
        DiscountRate = discountRate;
        _conflicts = new HashSet<string>(conflictsWith?.Select(c => c.Trim().ToUpperInvariant()) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks that the order has a positive pre-tax subtotal.
    /// </summary>
    /// <returns>
    /// True if the order subtotal is greater than zero; false otherwise.
    /// </returns>
    public bool IsEligible(OrderDraft draft, decimal subtotalBeforeDiscount, out string? ineligibleReason)
    {
        if (subtotalBeforeDiscount <= 0m)
        {
            ineligibleReason = $"{Code} requires at least one item in the order.";
            return false;
        }

        ineligibleReason = null;
        return true;
    }

    /// <summary>
    /// Multiplies the pre-tax subtotal by the percentage discount rate.
    /// </summary>
    /// <returns>
    /// Raw pre-tax discount amount.
    /// </returns>
    public decimal CalculateDiscount(OrderDraft draft, decimal subtotalBeforeDiscount)
    {
        if (subtotalBeforeDiscount <= 0m)
        {
            return 0m;
        }

        return subtotalBeforeDiscount * DiscountRate;
    }
}
