namespace PizzaShop.Core;

/// <summary>
/// Complete itemized receipt calculation for an order.
/// Every monetary property is rounded to whole cents (2 decimal places) so the numbers
/// shown to the customer always sum up cleanly without 1-cent rounding drift.
/// </summary>
public record PricingBreakdown(IReadOnlyList<LineItemBreakdown> Lines,
                               decimal SubtotalBeforeDiscount,
                               decimal DiscountAmount,
                               decimal TaxableSubtotal,
                               decimal TaxAmount,
                               decimal PreTipTotal,
                               decimal TipAmount,
                               decimal GrandTotal,
                               IReadOnlyList<string> AppliedPromos,
                               IReadOnlyList<string> Warnings);
