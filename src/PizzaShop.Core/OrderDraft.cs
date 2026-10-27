namespace PizzaShop.Core;

/// <summary>
/// Represents the customer's working order (line items, promo codes, and tip).
/// Used both in the CLI while building the order and in the server store once submitted.
/// </summary>
public class OrderDraft : ICloneable<OrderDraft>
{
    /// <summary>
    /// One or more pizza line items on the order.
    /// </summary>
    public List<OrderLineItem> Items { get; set; } = new List<OrderLineItem>();

    /// <summary>
    /// Applied promo codes. Kept as a list so the pricing engine can support non-conflicting stackable promos.
    /// </summary>
    public List<string> PromoCodes { get; set; } = new List<string>();

    /// <summary>
    /// Tip configuration (none, fixed dollar amount, or percentage of the bill).
    /// </summary>
    public TipInfo Tip { get; set; } = TipInfo.Zero;

    /// <summary>
    /// Creates an isolated copy of the order draft.
    /// </summary>
    /// <returns>
    /// A deep copy of the OrderDraft.
    /// </returns>
    public OrderDraft Clone()
    {
        return new OrderDraft
        {
            Items = Items.Select(i => i.Clone()).ToList(),
            PromoCodes = PromoCodes.ToList(),
            Tip = Tip
        };
    }

    /// <summary>
    /// Checks whether this draft has the exact same line items, promo codes, and tip as another draft.
    /// </summary>
    /// <returns>
    /// True if all items, promo codes, and tip match; otherwise false.
    /// </returns>
    public bool HasSameContentAs(OrderDraft? other)
    {
        if (other == null)
        {
            return false;
        }

        if (Tip != other.Tip || Items.Count != other.Items.Count || PromoCodes.Count != other.PromoCodes.Count)
        {
            return false;
        }

        for (int i = 0; i < Items.Count; i++)
        {
            if (!Items[i].HasSameContentAs(other.Items[i]))
            {
                return false;
            }
        }

        foreach (var code in PromoCodes)
        {
            if (!other.PromoCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }
}
