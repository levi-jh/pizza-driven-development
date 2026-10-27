namespace PizzaShop.Core;

/// <summary>
/// Strongly-typed deep-cloning contract used instead of the non-generic System.ICloneable
/// (which returns object and does not distinguish shallow vs. deep copies).
/// </summary>
public interface ICloneable<out T>
{
    /// <summary>
    /// Creates an isolated deep copy of the current instance.
    /// </summary>
    /// <returns>
    /// A new deep-copied instance of T.
    /// </returns>
    T Clone();
}
