using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PizzaShop.Core;

namespace PizzaShop.Cli;

/// <summary>
/// HTTP client wrapper used by the CLI to talk to PizzaShop.Api.
/// Handles passing the client RequestId and retrying transient timeouts
/// so unreliable mobile connections don't result in lost or double-charged orders.
/// </summary>
public class OrderServiceClient
{
    private const int MaxSubmitAttempts = 3;
    private const int BaseRetryDelayMs = 300;

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Demo flag toggled via option [f] in the CLI.
    /// When true, the first POST /api/orders call reaches the server (creating the order),
    /// but the client pretends the connection timed out before reading the response and automatically
    /// retries with the same RequestId.
    /// </summary>
    public bool SimulateDroppedResponseOnSubmit { get; set; }

    public OrderServiceClient(string baseUrl)
    {
        // Use InfiniteTimeSpan locally so stepping through breakpoints in PizzaShop.Api
        // doesn't cause the CLI to time out and fire retries while paused in the debugger.
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>
    /// Sends a new order to POST /api/orders with automatic retry on network or timeout errors.
    /// Uses the same requestId across all attempts so retries are deduplicated by the server.
    /// </summary>
    /// <returns>
    /// Tuple containing the created or replayed RemoteOrderDto, or an error message if submission failed.
    /// </returns>
    public async Task<(RemoteOrderDto? Order, string? Error)> SubmitOrderAsync(OrderDraft draft, decimal expectedTotal, string requestId)
    {
        var payload = BuildRequestPayload(draft, expectedTotal, null, requestId);

        for (int attempt = 1; attempt <= MaxSubmitAttempts; attempt++)
        {
            try
            {
                using var resp = await _http.PostAsJsonAsync("api/orders", payload, _jsonOptions);

                // If flaky simulation is active on attempt 1, pretend the mobile connection dropped
                // right after the server processed the POST so attempt 2 exercises retry deduplication.
                if (SimulateDroppedResponseOnSubmit && attempt == 1)
                {
                    throw new TaskCanceledException("Simulated mobile timeout while waiting for server response.");
                }

                if (resp.IsSuccessStatusCode)
                {
                    var order = await resp.Content.ReadFromJsonAsync<RemoteOrderDto>(_jsonOptions);
                    return (order, null);
                }

                var err = await ReadErrorAsync(resp);
                return (null, err);
            }
            catch (Exception ex)
            {
                if (attempt == MaxSubmitAttempts)
                {
                    return (null, $"Network failure after {MaxSubmitAttempts} attempts ({ex.Message}). Verify PizzaShop.Api is running at {_http.BaseAddress}.");
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [Network] Attempt {attempt}/{MaxSubmitAttempts} failed ({ex.Message}). Retrying with RequestId '{requestId}'...");
                Console.ResetColor();
                await Task.Delay(BaseRetryDelayMs * attempt);
            }
        }

        return (null, "Unexpected retry loop termination.");
    }

    /// <summary>
    /// Sends updated line items, promos, and tip to PUT /api/orders/{orderId}.
    /// Server will reject with 409 Conflict if the kitchen has already started the order
    /// or if expectedVersion doesn't match the server's current version.
    /// </summary>
    /// <returns>
    /// Tuple containing the updated RemoteOrderDto, or an error message if the update was rejected.
    /// </returns>
    public async Task<(RemoteOrderDto? Order, string? Error)> UpdateOrderAsync(Guid orderId, OrderDraft draft, decimal expectedTotal, int? expectedVersion = null)
    {
        var payload = BuildRequestPayload(draft, expectedTotal, expectedVersion);

        try
        {
            using var resp = await _http.PutAsJsonAsync($"api/orders/{orderId}", payload, _jsonOptions);
            if (resp.IsSuccessStatusCode)
            {
                var order = await resp.Content.ReadFromJsonAsync<RemoteOrderDto>(_jsonOptions);
                return (order, null);
            }

            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, $"Could not update order on service ({_http.BaseAddress}): {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the latest order status and pricing snapshot from GET /api/orders/{orderId}.
    /// </summary>
    /// <returns>
    /// Tuple containing the RemoteOrderDto, or an error message if the lookup failed.
    /// </returns>
    public async Task<(RemoteOrderDto? Order, string? Error)> GetOrderAsync(Guid orderId)
    {
        try
        {
            using var resp = await _http.GetAsync($"api/orders/{orderId}");
            if (resp.IsSuccessStatusCode)
            {
                var order = await resp.Content.ReadFromJsonAsync<RemoteOrderDto>(_jsonOptions);
                return (order, null);
            }

            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach order service ({_http.BaseAddress}): {ex.Message}");
        }
    }

    /// <summary>
    /// Requests cancellation via POST /api/orders/{orderId}/cancel.
    /// Only succeeds while the order is still NotStarted.
    /// </summary>
    /// <returns>
    /// Tuple containing the canceled RemoteOrderDto, or an error message if cancellation was rejected.
    /// </returns>
    public async Task<(RemoteOrderDto? Order, string? Error)> CancelOrderAsync(Guid orderId)
    {
        try
        {
            using var resp = await _http.PostAsync($"api/orders/{orderId}/cancel", null);
            if (resp.IsSuccessStatusCode)
            {
                var order = await resp.Content.ReadFromJsonAsync<RemoteOrderDto>(_jsonOptions);
                return (order, null);
            }

            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach order service ({_http.BaseAddress}): {ex.Message}");
        }
    }

    /// <summary>
    /// Calls the kitchen simulation endpoint (POST /api/orders/{orderId}/advance)
    /// to move the order to the next kitchen stage.
    /// </summary>
    /// <returns>
    /// Tuple containing the advanced RemoteOrderDto, or an error message if advancing failed.
    /// </returns>
    public async Task<(RemoteOrderDto? Order, string? Error)> AdvanceKitchenAsync(Guid orderId)
    {
        try
        {
            using var resp = await _http.PostAsync($"api/orders/{orderId}/advance", null);
            if (resp.IsSuccessStatusCode)
            {
                var order = await resp.Content.ReadFromJsonAsync<RemoteOrderDto>(_jsonOptions);
                return (order, null);
            }

            return (null, await ReadErrorAsync(resp));
        }
        catch (Exception ex)
        {
            return (null, $"Could not reach order service ({_http.BaseAddress}): {ex.Message}");
        }
    }

    /// <summary>
    /// Shapes the anonymous request body matching SubmitOrderRequest on the server.
    /// </summary>
    private static object BuildRequestPayload(OrderDraft draft, decimal expectedTotal, int? expectedVersion = null, string? requestId = null)
    {
        return new
        {
            Items = draft.Items.Select(i => new { i.SizeCode, i.Quantity, i.Toppings }).ToList(),
            PromoCodes = draft.PromoCodes,
            Tip = new { draft.Tip.Type, draft.Tip.Value },
            ExpectedTotal = expectedTotal,
            ExpectedVersion = expectedVersion,
            RequestId = requestId
        };
    }

    /// <summary>
    /// Extracts the server's error message from a non-2xx response, falling back to raw status details if the body isn't JSON.
    /// </summary>
    private async Task<string> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var errObj = await resp.Content.ReadFromJsonAsync<RemoteErrorDto>(_jsonOptions);
            if (!string.IsNullOrWhiteSpace(errObj?.Message))
            {
                return $"({(int)resp.StatusCode}) {errObj.Message}";
            }
        }
        catch
        {
            // Fall through to status code message if the error response wasn't JSON.
        }

        return $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})";
    }
}
