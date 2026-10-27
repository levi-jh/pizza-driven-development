using PizzaShop.Core;

namespace PizzaShop.Api;

/// <summary>
/// Internal mutable record kept inside OrderStore.
/// Never exposed outside the semaphore directly; callers always receive a cloned OrderResponse.
/// </summary>
internal sealed class StoredOrder
{
    public StoredOrder(OrderDraft draft, PricingBreakdown pricing)
    {
        Draft = draft;
        Pricing = pricing;
    }

    public Guid OrderId { get; init; }
    public OrderStatus Status { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public OrderDraft Draft { get; set; }
    public PricingBreakdown Pricing { get; set; }

    public OrderResponse ToResponse(OrderFormatter formatter)
    {
        return new OrderResponse(OrderId, Status, OrderFormatter.FormatStatus(Status), Version, CreatedAtUtc, UpdatedAtUtc, formatter.DescribeOrder(Draft, Pricing), Draft.Clone(), Pricing);
    }
}
