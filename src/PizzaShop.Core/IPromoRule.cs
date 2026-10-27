namespace PizzaShop.Core;

/// <summary>
/// Contract for promotional discounts.
/// Keeping eligibility, conflict rules, and discount math behind this interface allows
/// new promos to be added without modifying PricingCalculator.
/// </summary>
public interface IPromoRule
{
    /// <summary>
    /// Uppercase promo code entered by the customer (e.g. "ILOVEPIZZA").
    /// </summary>
    string Code { get; }

    /// <summary>
    /// Human-readable explanation of what the promo does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Codes of other promos that cannot be combined with this one.
    /// Checked in both directions by PricingCalculator.
    /// </summary>
    IReadOnlyCollection<string> ConflictsWith { get; }

    /// <summary>
    /// Checks whether the current order satisfies the promo's preconditions (minimum spend, required items, etc.).
    /// </summary>
    /// <returns>
    /// True if the order meets the promo requirements; false otherwise.
    /// </returns>
    bool IsEligible(OrderDraft draft, decimal subtotalBeforeDiscount, out string? ineligibleReason);

    /// <summary>
    /// Computes the raw pre-tax discount amount for the order.
    /// Caller is responsible for cent-rounding and clamping to the subtotal.
    /// </summary>
    /// <returns>
    /// Unrounded pre-tax discount amount.
    /// </returns>
    decimal CalculateDiscount(OrderDraft draft, decimal subtotalBeforeDiscount);
}
