using PizzaShop.Core;

namespace PizzaShop.Api;

/// <summary>
/// Request payload for POST /api/orders and PUT /api/orders/{id}.
/// The client sends the structural order contents rather than prices; the server recalculates
/// the total via PricingCalculator and compares it against ExpectedTotal if provided.
/// </summary>
public record SubmitOrderRequest(List<OrderLineItemDto> Items, List<string>? PromoCodes = null, TipDto? Tip = null, decimal? ExpectedTotal = null, int? ExpectedVersion = null, string? RequestId = null)
{
    /// <summary>
    /// Maps the incoming DTO into a normalized OrderDraft for validation and pricing.
    /// </summary>
    /// <returns>
    /// Normalized OrderDraft instance.
    /// </returns>
    public OrderDraft ToOrderDraft()
    {
        var tipInfo = TipInfo.Zero;
        if (Tip != null)
        {
            tipInfo = new TipInfo(Tip.Type, Tip.Value);
        }

        var draft = new OrderDraft
        {
            PromoCodes = PromoCodes?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().ToUpperInvariant()).ToList() ?? new List<string>(),
            Tip = tipInfo
        };

        if (Items != null)
        {
            foreach (var dto in Items)
            {
                var line = new OrderLineItem
                {
                    SizeCode = dto.SizeCode?.Trim().ToLowerInvariant() ?? "",
                    Quantity = dto.Quantity
                };

                if (dto.Toppings != null)
                {
                    foreach (var t in dto.Toppings.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        line.AddTopping(t);
                    }
                }

                draft.Items.Add(line);
            }
        }

        return draft;
    }
}
