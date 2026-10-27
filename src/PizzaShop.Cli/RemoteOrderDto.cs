using PizzaShop.Core;

namespace PizzaShop.Cli;

/// <summary>
/// Client-side representation of the order snapshot returned by the Order Service.
/// </summary>
public record RemoteOrderDto(Guid OrderId,
                             OrderStatus Status,
                             string StatusDisplay,
                             int Version,
                             DateTimeOffset CreatedAtUtc,
                             DateTimeOffset UpdatedAtUtc,
                             string Description,
                             OrderDraft Order,
                             PricingBreakdown Pricing);
