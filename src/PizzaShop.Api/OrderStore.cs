using Microsoft.Extensions.Logging.Abstractions;
using PizzaShop.Core;

namespace PizzaShop.Api;

/// <summary>
/// Thread-safe in-memory store managing order persistence, retry deduplication, and lifecycle state transitions.
/// Even though storage is in-memory for this exercise, all public operations are async/await-first
/// using SemaphoreSlim.WaitAsync so the service contract doesn't have to change when backed by a database.
/// For the 3-hour timebox, we use a single process-wide SemaphoreSlim across all orders.
/// </summary>
public class OrderStore
{
    private const int InitialOrderVersion = 1;

    // Semaphore across all orders. Simple and deadlock-free for the timebox,
    // though it contends across unrelated orders under heavy load.
    private readonly SemaphoreSlim _sync = new SemaphoreSlim(1, 1);
    private readonly Dictionary<Guid, StoredOrder> _orders = new Dictionary<Guid, StoredOrder>();

    // Maps client-supplied RequestId values to the created OrderId so mobile timeout retries return the existing order.
    // We don't hash the request payload to verify the retry body matches the original body, and we don't expire old keys on a TTL timer.
    private readonly Dictionary<string, Guid> _processedRequestIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
    private readonly PricingCalculator _calculator;
    private readonly OrderFormatter _formatter;
    private readonly ILogger<OrderStore> _logger;

    public OrderStore(PricingCalculator calculator, OrderFormatter formatter, ILogger<OrderStore>? logger = null)
    {
        _calculator = calculator;
        _formatter = formatter;
        _logger = logger ?? NullLogger<OrderStore>.Instance;
    }

    /// <summary>
    /// Validates, prices, and stores a new order in NotStarted state.
    /// </summary>
    /// <returns>
    /// Tuple containing the created or replayed OrderResponse, whether a new record was created, the HTTP status code, and any validation error.
    /// </returns>
    public async Task<(OrderResponse? Response, bool CreatedNew, int HttpStatus, ApiErrorResponse? Error)> CreateOrderAsync(SubmitOrderRequest request, CancellationToken cancellationToken = default)
    {
        string? normalizedReqId = null;
        if (!string.IsNullOrWhiteSpace(request?.RequestId))
        {
            normalizedReqId = request.RequestId.Trim();
        }

        // Fast-path check inside semaphore if this is a retry for an already-created order.
        if (normalizedReqId != null)
        {
            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_processedRequestIds.TryGetValue(normalizedReqId, out var existingId) && _orders.TryGetValue(existingId, out var existingOrder))
                {
                    _logger.LogInformation("Duplicate order submission replayed for RequestId {RequestId}; returning existing Order {OrderId} (v{Version}).", normalizedReqId, existingOrder.OrderId, existingOrder.Version);
                    return (existingOrder.ToResponse(_formatter), false, StatusCodes.Status200OK, null);
                }
            }
            finally
            {
                _sync.Release();
            }
        }

        // Run validation and pricing outside the semaphore since PricingCalculator is thread-safe.
        if (!TryValidateAndPrice(request!, out var draft, out var pricing, out var validationError, out var errorStatus))
        {
            return (null, false, errorStatus, validationError);
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the semaphore in case two identical retry requests arrived simultaneously.
            if (normalizedReqId != null && _processedRequestIds.TryGetValue(normalizedReqId, out var existingId) && _orders.TryGetValue(existingId, out var existingOrder))
            {
                _logger.LogInformation("Concurrent duplicate submission caught inside semaphore for RequestId {RequestId}; returning existing Order {OrderId} (v{Version}).", normalizedReqId, existingOrder.OrderId, existingOrder.Version);
                return (existingOrder.ToResponse(_formatter), false, StatusCodes.Status200OK, null);
            }

            var now = DateTimeOffset.UtcNow;
            var stored = new StoredOrder(draft, pricing)
            {
                OrderId = Guid.NewGuid(),
                Status = OrderStatus.NotStarted,
                Version = InitialOrderVersion,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _orders[stored.OrderId] = stored;
            if (normalizedReqId != null)
            {
                _processedRequestIds[normalizedReqId] = stored.OrderId;
            }

            _logger.LogInformation("Created Order {OrderId} (Items: {ItemCount}, Total: ${GrandTotal:0.00}, RequestId: {RequestId}).", stored.OrderId, draft.Items.Count, pricing.GrandTotal, normalizedReqId ?? "none");
            return (stored.ToResponse(_formatter), true, StatusCodes.Status201Created, null);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Looks up a single order by ID.
    /// </summary>
    /// <returns>
    /// The matching OrderResponse, or null if the order ID does not exist.
    /// </returns>
    public async Task<OrderResponse?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                return order.ToResponse(_formatter);
            }

            _logger.LogDebug("Order {OrderId} was not found during lookup.", orderId);
            return null;
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Lists all stored orders ordered newest-first.
    /// </summary>
    /// <returns>
    /// Read-only list of OrderResponse snapshots.
    /// </returns>
    public async Task<IReadOnlyList<OrderResponse>> ListOrdersAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            return _orders.Values.OrderByDescending(o => o.CreatedAtUtc).Select(o => o.ToResponse(_formatter)).ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Replaces the items, promos, and tip on an existing order while it is still NotStarted.
    /// </summary>
    /// <returns>
    /// Tuple containing the updated OrderResponse, HTTP status code (200, 400, 404, or 409), and any error details.
    /// </returns>
    public async Task<(OrderResponse? Response, int HttpStatus, ApiErrorResponse? Error)> UpdateOrderAsync(Guid orderId, SubmitOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryValidateAndPrice(request, out var draft, out var pricing, out var validationError, out var errorStatus))
        {
            return (null, errorStatus, validationError);
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                _logger.LogWarning("Rejected update for unknown Order {OrderId}.", orderId);
                return (null, StatusCodes.Status404NotFound, new ApiErrorResponse($"Order '{orderId}' not found.", "ORDER_NOT_FOUND"));
            }

            if (order.Status != OrderStatus.NotStarted)
            {
                _logger.LogWarning("Rejected update for Order {OrderId}: status is already {Status} (v{Version}).", orderId, order.Status, order.Version);
                return (null, StatusCodes.Status409Conflict, new ApiErrorResponse($"Order cannot be modified because its status is '{OrderFormatter.FormatStatus(order.Status)}'. Updates are only allowed while 'Not Started'.", "ORDER_LOCKED"));
            }

            if (request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != order.Version)
            {
                _logger.LogWarning("Rejected update for Order {OrderId} due to version conflict (client sent v{ExpectedVersion}, server is at v{ActualVersion}).", orderId, request.ExpectedVersion.Value, order.Version);
                return (null, StatusCodes.Status409Conflict, new ApiErrorResponse($"Order version mismatch (expected v{request.ExpectedVersion.Value}, but server is at v{order.Version}). Refresh the order before updating.", "VERSION_CONFLICT"));
            }

            order.Draft = draft;
            order.Pricing = pricing;
            order.Version++;
            order.UpdatedAtUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Updated Order {OrderId} to v{Version} (Items: {ItemCount}, Total: ${GrandTotal:0.00}).", orderId, order.Version, draft.Items.Count, pricing.GrandTotal);
            return (order.ToResponse(_formatter), StatusCodes.Status200OK, null);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Cancels an order if it is still NotStarted.
    /// Rejects orders that are already canceled or already started by the kitchen with 409 Conflict.
    /// </summary>
    /// <returns>
    /// Tuple containing the canceled OrderResponse, HTTP status code (200, 404, or 409), and any error details.
    /// </returns>
    public async Task<(OrderResponse? Response, int HttpStatus, ApiErrorResponse? Error)> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                _logger.LogWarning("Rejected cancel for unknown Order {OrderId}.", orderId);
                return (null, StatusCodes.Status404NotFound, new ApiErrorResponse($"Order '{orderId}' not found.", "ORDER_NOT_FOUND"));
            }

            if (order.Status == OrderStatus.Canceled)
            {
                _logger.LogWarning("Rejected cancellation for Order {OrderId}: order is already canceled (v{Version}).", orderId, order.Version);
                return (null, StatusCodes.Status409Conflict, new ApiErrorResponse("Order has already been canceled and is closed.", "ORDER_ALREADY_CANCELED"));
            }

            if (order.Status != OrderStatus.NotStarted)
            {
                _logger.LogWarning("Rejected cancellation for Order {OrderId}: kitchen already advanced status to {Status} (v{Version}).", orderId, order.Status, order.Version);
                return (null, StatusCodes.Status409Conflict, new ApiErrorResponse($"Order cannot be canceled because its status is '{OrderFormatter.FormatStatus(order.Status)}'. Cancellation is only allowed while 'Not Started'.", "ORDER_ALREADY_STARTED"));
            }

            order.Status = OrderStatus.Canceled;
            order.Version++;
            order.UpdatedAtUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Canceled Order {OrderId} (v{Version}).", orderId, order.Version);
            return (order.ToResponse(_formatter), StatusCodes.Status200OK, null);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Kitchen simulator transition: steps an order forward one stage along NotStarted -> Started -> ReadyForPickup -> Delivered.
    /// </summary>
    /// <returns>
    /// Tuple containing the updated OrderResponse, HTTP status code (200, 404, or 409), and any error details.
    /// </returns>
    public async Task<(OrderResponse? Response, int HttpStatus, ApiErrorResponse? Error)> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                _logger.LogWarning("Rejected kitchen advance for unknown Order {OrderId}.", orderId);
                return (null, StatusCodes.Status404NotFound, new ApiErrorResponse($"Order '{orderId}' not found.", "ORDER_NOT_FOUND"));
            }

            var previousStatus = order.Status;
            OrderStatus nextStatus;
            switch (order.Status)
            {
                case OrderStatus.NotStarted:
                    nextStatus = OrderStatus.Started;
                    break;
                case OrderStatus.Started:
                    nextStatus = OrderStatus.ReadyForPickup;
                    break;
                case OrderStatus.ReadyForPickup:
                    nextStatus = OrderStatus.Delivered;
                    break;
                case OrderStatus.Canceled:
                    _logger.LogWarning("Rejected kitchen advance for Order {OrderId}: order was already canceled (v{Version}).", orderId, order.Version);
                    return (null, StatusCodes.Status409Conflict, new ApiErrorResponse("Cannot advance a canceled order.", "ORDER_CANCELED"));
                case OrderStatus.Delivered:
                default:
                    _logger.LogWarning("Rejected kitchen advance for Order {OrderId}: order was already delivered (v{Version}).", orderId, order.Version);
                    return (null, StatusCodes.Status409Conflict, new ApiErrorResponse("Order has already been delivered.", "ORDER_ALREADY_DELIVERED"));
            }

            order.Status = nextStatus;
            order.Version++;
            order.UpdatedAtUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Kitchen advanced Order {OrderId} from {PreviousStatus} to {NewStatus} (v{Version}).", orderId, previousStatus, nextStatus, order.Version);
            return (order.ToResponse(_formatter), StatusCodes.Status200OK, null);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Used by the optional background kitchen timer to step all in-progress orders forward.
    /// </summary>
    /// <returns>
    /// Number of active orders that were advanced.
    /// </returns>
    public async Task<int> AdvanceAllActiveOrdersOneStepAsync(CancellationToken cancellationToken = default)
    {
        int count = 0;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            foreach (var order in _orders.Values)
            {
                if (order.Status == OrderStatus.NotStarted || order.Status == OrderStatus.Started || order.Status == OrderStatus.ReadyForPickup)
                {
                    var prev = order.Status;
                    if (order.Status == OrderStatus.NotStarted)
                    {
                        order.Status = OrderStatus.Started;
                    }
                    else if (order.Status == OrderStatus.Started)
                    {
                        order.Status = OrderStatus.ReadyForPickup;
                    }
                    else
                    {
                        order.Status = OrderStatus.Delivered;
                    }

                    order.Version++;
                    order.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    count++;
                    _logger.LogDebug("Background timer advanced Order {OrderId} from {PreviousStatus} to {NewStatus} (v{Version}).", order.OrderId, prev, order.Status, order.Version);
                }
            }
        }
        finally
        {
            _sync.Release();
        }
        return count;
    }

    /// <summary>
    /// Shared validation and server-side pricing check for both Create and Update requests.
    /// </summary>
    private bool TryValidateAndPrice(SubmitOrderRequest request, out OrderDraft draft, out PricingBreakdown pricing, out ApiErrorResponse? error, out int httpStatus)
    {
        draft = null!;
        pricing = null!;

        if (request?.Items == null || request.Items.Count == 0)
        {
            _logger.LogWarning("Order validation failed ({ErrorCode}): request contained no line items.", "EMPTY_ORDER");
            error = new ApiErrorResponse("An order must contain at least one line item.", "EMPTY_ORDER");
            httpStatus = StatusCodes.Status400BadRequest;
            return false;
        }

        try
        {
            draft = request.ToOrderDraft();
            pricing = _calculator.Calculate(draft);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Order validation failed ({ErrorCode}): {Message}", "INVALID_ORDER_DATA", ex.Message);
            error = new ApiErrorResponse(ex.Message, "INVALID_ORDER_DATA");
            httpStatus = StatusCodes.Status400BadRequest;
            return false;
        }

        // If the client submitted a promo code whose threshold isn't met (e.g. SAVE5 on a $15 order),
        // reject the submission with 400 Bad Request rather than silently dropping the promo on the server.
        if (pricing.Warnings.Count > 0)
        {
            var warningText = string.Join(" ", pricing.Warnings);
            _logger.LogWarning("Order validation failed ({ErrorCode}): {Warnings}", "INELIGIBLE_PROMO", warningText);
            error = new ApiErrorResponse(warningText, "INELIGIBLE_PROMO");
            httpStatus = StatusCodes.Status400BadRequest;
            return false;
        }

        // Ensure the total the customer sees while building the order matches the total the restaurant charges.
        if (request.ExpectedTotal.HasValue && request.ExpectedTotal.Value != pricing.GrandTotal)
        {
            _logger.LogWarning("Order validation failed ({ErrorCode}): client expected ${ExpectedTotal:0.00} but server calculated ${ServerTotal:0.00}.", "PRICE_MISMATCH", request.ExpectedTotal.Value, pricing.GrandTotal);
            error = new ApiErrorResponse($"Client expected total ${request.ExpectedTotal.Value:0.00} does not match server calculated total ${pricing.GrandTotal:0.00}.", "PRICE_MISMATCH");
            httpStatus = StatusCodes.Status400BadRequest;
            return false;
        }

        error = null;
        httpStatus = StatusCodes.Status200OK;
        return true;
    }
}
