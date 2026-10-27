using System.Text.Json.Serialization;
using PizzaShop.Api;
using PizzaShop.Core;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums as readable strings ("NotStarted", "Percentage", etc.) over the wire.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Load the menu catalog asynchronously from menu.json (falling back to CreateDefault() if not found or malformed)
// and register the pricing calculator, formatter, and order store as DI singletons.
var catalog = await MenuCatalog.LoadFromFileOrDefaultAsync();
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton<PricingCalculator>();
builder.Services.AddSingleton<OrderFormatter>();
builder.Services.AddSingleton<OrderStore>();

// Optional background timer to auto-advance kitchen orders if KITCHEN_AUTO_ADVANCE_SECONDS > 0.
// Left off by default so manual /advance calls and automated tests stay deterministic.
var autoAdvanceEnv = Environment.GetEnvironmentVariable("KITCHEN_AUTO_ADVANCE_SECONDS");
if (int.TryParse(autoAdvanceEnv, out var autoSeconds) && autoSeconds > 0)
{
    builder.Services.AddHostedService(sp => new KitchenAutoAdvanceWorker(sp.GetRequiredService<OrderStore>(), sp.GetRequiredService<ILogger<KitchenAutoAdvanceWorker>>(), TimeSpan.FromSeconds(autoSeconds)));
}

var app = builder.Build();

app.Logger.LogInformation("PizzaShop Order Service initialized with {SizeCount} sizes, {CategoryCount} topping categories, {ToppingCount} toppings, and {PromoCount} promo rules (TaxRate: {TaxRate:P1}).",
                          catalog.Sizes.Count,
                          catalog.Categories.Count,
                          catalog.Toppings.Count,
                          catalog.Promos.Count,
                          catalog.TaxRate);


// In most circumstances we'd do full controllers, but for time's sake we'll use minimal API.
// With controller's we'd do proper OpenApi documentation, in this case since it was not required in the takehome we will skip it but call it out in the readme.


// GET /api/menu - Returns the active menu catalog and promo rules.
app.MapGet("/api/menu", (MenuCatalog menu) => Results.Ok(new
{
    TaxRate = menu.TaxRate,
    Sizes = menu.Sizes.Values,
    Categories = menu.Categories.Values,
    Toppings = menu.Toppings.Values,
    Promos = menu.Promos.Values.Select(p => new
    {
        p.Code,
        p.Description,
        p.ConflictsWith
    })
}));

// GET /api/orders - Lists all orders (handy for debugging and inspection).
app.MapGet("/api/orders", async (OrderStore store, CancellationToken ct) =>
{
    var orders = await store.ListOrdersAsync(ct);
    return Results.Ok(orders);
});

// POST /api/orders - Submits a new order. Uses RequestId on the payload to make client retries safe.
app.MapPost("/api/orders", async (SubmitOrderRequest request, OrderStore store, CancellationToken ct) =>
{
    var result = await store.CreateOrderAsync(request, ct);

    if (result.Error != null)
    {
        return Results.Json(result.Error, statusCode: result.HttpStatus);
    }

    if (result.CreatedNew)
    {
        return Results.Created($"/api/orders/{result.Response!.OrderId}", result.Response);
    }

    return Results.Ok(result.Response);
});

// GET /api/orders/{id} - Queries current status and receipt breakdown for an order.
app.MapGet("/api/orders/{id:guid}", async (Guid id, OrderStore store, CancellationToken ct) =>
{
    var order = await store.GetOrderAsync(id, ct);
    if (order == null)
    {
        return Results.NotFound(new ApiErrorResponse($"Order '{id}' not found.", "ORDER_NOT_FOUND"));
    }

    return Results.Ok(order);
});

// PUT /api/orders/{id} - Modifies an existing order (only allowed while NotStarted).
app.MapPut("/api/orders/{id:guid}", async (Guid id, SubmitOrderRequest request, OrderStore store, CancellationToken ct) =>
{
    var result = await store.UpdateOrderAsync(id, request, ct);
    if (result.Error != null)
    {
        return Results.Json(result.Error, statusCode: result.HttpStatus);
    }

    return Results.Ok(result.Response);
});

// POST /api/orders/{id}/cancel - Cancels an order (only allowed while NotStarted).
app.MapPost("/api/orders/{id}/cancel", async (Guid id, OrderStore store, CancellationToken ct) =>
{
    var result = await store.CancelOrderAsync(id, ct);
    if (result.Error != null)
    {
        return Results.Json(result.Error, statusCode: result.HttpStatus);
    }

    return Results.Ok(result.Response);
});

// POST /api/orders/{id}/advance - Kitchen simulation endpoint to step order status forward.
app.MapPost("/api/orders/{id}/advance", async (Guid id, OrderStore store, CancellationToken ct) =>
{
    var result = await store.AdvanceStatusAsync(id, ct);
    if (result.Error != null)
    {
        return Results.Json(result.Error, statusCode: result.HttpStatus);
    }

    return Results.Ok(result.Response);
});

await app.RunAsync();
