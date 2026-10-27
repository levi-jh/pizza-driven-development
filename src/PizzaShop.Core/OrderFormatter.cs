using System.Text;

namespace PizzaShop.Core;

/// <summary>
/// Formats human-readable descriptions of orders and statuses.
/// Registered as an instance service in DI rather than a static utility class.
/// </summary>
public class OrderFormatter
{
    private readonly MenuCatalog _catalog;
    private readonly PricingCalculator _calculator;

    public OrderFormatter(MenuCatalog catalog, PricingCalculator calculator)
    {
        _catalog = catalog;
        _calculator = calculator;
    }

    /// <summary>
    /// Generates the written description of the order including quantity, size, and toppings
    /// for each line item, followed by the financial breakdown.
    /// </summary>
    /// <returns>
    /// Multi-line formatted order summary string.
    /// </returns>
    public string DescribeOrder(OrderDraft draft, PricingBreakdown? pricing = null)
    {
        if (draft == null || draft.Items.Count == 0)
        {
            return "Order is currently empty.";
        }

        if (pricing == null)
        {
            pricing = _calculator.Calculate(draft);
        }

        var sb = new StringBuilder();
        sb.AppendLine("Order Summary:");

        for (int i = 0; i < draft.Items.Count; i++)
        {
            var item = draft.Items[i];
            var sizeName = item.SizeCode;
            if (_catalog.TryGetSize(item.SizeCode, out var s))
            {
                sizeName = s.Name;
            }

            var toppingNames = new List<string>();
            foreach (var t in item.Toppings)
            {
                if (_catalog.TryGetTopping(t, out var td, out _))
                {
                    toppingNames.Add(td.Name.ToLowerInvariant());
                }
                else
                {
                    toppingNames.Add(t);
                }
            }

            var toppingText = "plain (no toppings)";
            if (toppingNames.Count > 0)
            {
                toppingText = $"with {string.Join(", ", toppingNames)}";
            }

            var lineTotalText = string.Empty;
            if (i < pricing.Lines.Count)
            {
                lineTotalText = $" - ${pricing.Lines[i].LineTotal:0.00}";
            }

            sb.AppendLine($"  {i + 1}. {item.Quantity}x {sizeName} pizza {toppingText}{lineTotalText}");
        }

        sb.AppendLine($"Subtotal: ${pricing.SubtotalBeforeDiscount:0.00}");

        if (pricing.DiscountAmount > 0)
        {
            sb.AppendLine($"Promo ({string.Join(", ", pricing.AppliedPromos)}): -${pricing.DiscountAmount:0.00}");
        }

        foreach (var warning in pricing.Warnings)
        {
            sb.AppendLine($"Note: {warning}");
        }

        sb.AppendLine($"Tax ({_catalog.TaxRate * PricingCalculator.PercentToDecimalFactor:0.#}%): ${pricing.TaxAmount:0.00}");

        if (draft.Tip.Type == TipType.Percentage && draft.Tip.Value > 0)
        {
            sb.AppendLine($"Tip ({draft.Tip.Value:0.##}%): ${pricing.TipAmount:0.00}");
        }
        else if (draft.Tip.Type == TipType.FixedDollars && draft.Tip.Value > 0)
        {
            sb.AppendLine($"Tip: ${pricing.TipAmount:0.00}");
        }
        else
        {
            sb.AppendLine("Tip: $0.00");
        }

        sb.Append($"Total: ${pricing.GrandTotal:0.00}");
        return sb.ToString();
    }

    /// <summary>
    /// Maps an OrderStatus enum value to the display string required by the spec.
    /// </summary>
    /// <returns>
    /// Human-readable status label (e.g. "Not Started", "Ready for Pickup").
    /// </returns>
    public static string FormatStatus(OrderStatus status)
    {
        switch (status)
        {
            case OrderStatus.NotStarted:
                return "Not Started";
            case OrderStatus.Started:
                return "Started";
            case OrderStatus.ReadyForPickup:
                return "Ready for Pickup";
            case OrderStatus.Delivered:
                return "Delivered";
            case OrderStatus.Canceled:
                return "Canceled";
            default:
                return status.ToString();
        }
    }
}
