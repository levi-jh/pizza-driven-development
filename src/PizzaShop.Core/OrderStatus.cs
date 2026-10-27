namespace PizzaShop.Core;

/// <summary>
/// Lifecycle states for a submitted order.
/// Customer updates and cancellations are only valid while NotStarted.
/// </summary>
public enum OrderStatus
{
    NotStarted,
    Started,
    ReadyForPickup,
    Delivered,
    // Explicit terminal state so GET /api/orders/{id} still returns a clear status after cancellation instead of 404ing.
    Canceled
}
