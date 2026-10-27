namespace PizzaShop.Core;

/// <summary>
/// A single entry on the order (e.g. 2x Large pizzas with pepperoni and bacon).
/// Left mutable so the CLI builder and JSON serializer can work with it directly.
/// </summary>
public class OrderLineItem : ICloneable<OrderLineItem>
{
    /// <summary>
    /// Code matching a size in MenuCatalog.Sizes (e.g. "small", "medium", "large").
    /// </summary>
    public string SizeCode { get; set; } = "medium";

    /// <summary>
    /// How many pizzas of this exact size and topping combo to make. Must be at least 1.
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Topping codes applied to this pizza. Treated as a distinct set per pizza.
    /// </summary>
    public List<string> Toppings { get; set; } = new List<string>();

    /// <summary>
    /// Adds a topping to the line item if it isn't already on the pizza.
    /// </summary>
    /// <returns>
    /// True if the topping was added; false if the code is blank or the topping was already present.
    /// </returns>
    public bool AddTopping(string? toppingCode)
    {
        if (string.IsNullOrWhiteSpace(toppingCode))
        {
            return false;
        }

        var normalized = toppingCode.Trim().ToLowerInvariant();
        if (Toppings.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Toppings.Add(normalized);
        return true;
    }

    /// <summary>
    /// Removes a topping by code (case-insensitive).
    /// </summary>
    /// <returns>
    /// True if the topping was removed; false if the code is blank or wasn't on the pizza.
    /// </returns>
    public bool RemoveTopping(string? toppingCode)
    {
        if (string.IsNullOrWhiteSpace(toppingCode))
        {
            return false;
        }

        var idx = Toppings.FindIndex(t => string.Equals(t, toppingCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        Toppings.RemoveAt(idx);
        return true;
    }

    /// <summary>
    /// Deep-copies the line item so stored orders can't be mutated by caller references.
    /// </summary>
    /// <returns>
    /// A new OrderLineItem copy.
    /// </returns>
    public OrderLineItem Clone()
    {
        return new OrderLineItem
        {
            SizeCode = SizeCode,
            Quantity = Quantity,
            Toppings = Toppings.ToList()
        };
    }

    /// <summary>
    /// Checks whether another line item has the same size, quantity, and toppings.
    /// </summary>
    /// <returns>
    /// True if both line items match in size, quantity, and topping set; otherwise false.
    /// </returns>
    public bool HasSameContentAs(OrderLineItem? other)
    {
        if (other == null)
        {
            return false;
        }

        if (Quantity != other.Quantity || !string.Equals(SizeCode, other.SizeCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Toppings.Count != other.Toppings.Count)
        {
            return false;
        }

        foreach (var topping in Toppings)
        {
            if (!other.Toppings.Any(t => string.Equals(t, topping, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }
}
