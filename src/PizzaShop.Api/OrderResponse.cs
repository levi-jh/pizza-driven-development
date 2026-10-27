using PizzaShop.Core;

namespace PizzaShop.Api;

/// <summary>
/// Snapshot of an order returned by the Order Service API.
/// </summary>
public record OrderResponse(Guid OrderId,
                            OrderStatus Status,
                            string StatusDisplay,
                            int Version,
                            DateTimeOffset CreatedAtUtc,
                            DateTimeOffset UpdatedAtUtc,
                            string Description,
                            OrderDraft Order,
                            PricingBreakdown Pricing);
