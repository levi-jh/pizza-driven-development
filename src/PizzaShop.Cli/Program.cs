using PizzaShop.Cli;
using PizzaShop.Core;

// Interactive console Order Builder. Kept as a straightforward procedural loop in Program.cs using System.Console.

var apiUrl = Environment.GetEnvironmentVariable("PIZZASHOP_API_URL") ?? "http://localhost:5080";
if (args.Length >= 2 && args[0] == "--url")
{
    apiUrl = args[1];
}

var catalog = await MenuCatalog.LoadFromFileOrDefaultAsync();
var calculator = new PricingCalculator(catalog);
var formatter = new OrderFormatter(catalog, calculator);
var client = new OrderServiceClient(apiUrl);

var draft = new OrderDraft();
// Generated once per draft so if POST /api/orders times out and retries, the server deduplicates it.
// TODO (cleanup): These session variables grew organically as remote sync and banners were added;
// wrap them into a single CliSessionState class so we aren't passing ref/out parameters or callbacks to the handlers below.
string currentRequestId = Guid.NewGuid().ToString("N");
Guid? activeRemoteOrderId = null;
string? lastRemoteStatus = null;
int? lastRemoteVersion = null;
OrderDraft? lastSyncedDraft = null;
string? statusBanner = null;
bool isErrorBanner = false;

bool running = true;
while (running)
{
    // Recalculate the running total automatically on every iteration after any item, topping, tip, or promo modification.
    var pricing = calculator.Calculate(draft);

    RenderScreen(draft, pricing, catalog, apiUrl, activeRemoteOrderId, lastRemoteStatus, lastSyncedDraft, client.SimulateDroppedResponseOnSubmit, statusBanner, isErrorBanner);
    statusBanner = null;
    isErrorBanner = false;

    Console.Write("Select option > ");
    var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
    Console.WriteLine();

    bool isOrderCanceled = activeRemoteOrderId.HasValue && string.Equals(lastRemoteStatus, OrderFormatter.FormatStatus(OrderStatus.Canceled), StringComparison.OrdinalIgnoreCase);
    if (isOrderCanceled && (choice == "1" || choice == "2" || choice == "3" || choice == "4" || choice == "5" || choice == "7" || choice == "9" || choice == "k"))
    {
        statusBanner = "This order has been canceled and is closed. Press [n] to start a new order or [l] to load another order.";
        isErrorBanner = true;
        continue;
    }

    bool isOrderDelivered = activeRemoteOrderId.HasValue && string.Equals(lastRemoteStatus, OrderFormatter.FormatStatus(OrderStatus.Delivered), StringComparison.OrdinalIgnoreCase);
    if (isOrderDelivered && (choice == "1" || choice == "2" || choice == "3" || choice == "4" || choice == "5" || choice == "7" || choice == "9" || choice == "k"))
    {
        statusBanner = "This order has already been delivered and is closed. Press [n] to start a new order or [l] to load another order.";
        isErrorBanner = true;
        continue;
    }

    switch (choice)
    {
        case "1":
            HandleAddLineItem(draft, catalog, out statusBanner, out isErrorBanner);
            break;
        case "2":
            HandleEditLineItem(draft, catalog, out statusBanner, out isErrorBanner);
            break;
        case "3":
            HandleRemoveLineItem(draft, out statusBanner, out isErrorBanner);
            break;
        case "4":
            HandleSetTip(draft, out statusBanner, out isErrorBanner);
            break;
        case "5":
            HandlePromos(draft, catalog, calculator, out statusBanner, out isErrorBanner);
            break;
        case "6":
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine(formatter.DescribeOrder(draft, pricing));
            Console.WriteLine("------------------------------------------------------------");
            Pause();
            break;
        case "7":
            (statusBanner, isErrorBanner) = await HandleSubmitOrUpdateAsync(draft, pricing, client, currentRequestId, activeRemoteOrderId, lastRemoteVersion, lastSyncedDraft, (id, status, version, syncedSnapshot) =>
            {
                activeRemoteOrderId = id;
                lastRemoteStatus = status;
                lastRemoteVersion = version;
                lastSyncedDraft = syncedSnapshot;
            });
            break;
        case "8":
            (statusBanner, isErrorBanner) = await HandleRefreshStatusAsync(client, activeRemoteOrderId, (status, version) =>
            {
                lastRemoteStatus = status;
                lastRemoteVersion = version;
            });
            break;
        case "9":
            (statusBanner, isErrorBanner) = await HandleCancelRemoteOrderAsync(client, activeRemoteOrderId, (status, version) =>
            {
                lastRemoteStatus = status;
                lastRemoteVersion = version;
            });
            break;
        case "k":
            (statusBanner, isErrorBanner) = await HandleKitchenAdvanceAsync(client, activeRemoteOrderId, (status, version) =>
            {
                lastRemoteStatus = status;
                lastRemoteVersion = version;
            });
            break;
        case "f":
            client.SimulateDroppedResponseOnSubmit = !client.SimulateDroppedResponseOnSubmit;
            if (client.SimulateDroppedResponseOnSubmit)
            {
                statusBanner = "Simulated mobile timeout on submit is now ON.";
            }
            else
            {
                statusBanner = "Simulated mobile timeout on submit is now OFF.";
            }
            break;
        case "n":
            StartNewOrderDraft(ref draft, ref currentRequestId, ref activeRemoteOrderId, ref lastRemoteStatus, ref lastRemoteVersion, ref lastSyncedDraft, out statusBanner);
            break;
        case "l":
            (statusBanner, isErrorBanner) = await HandleLoadOrderAsync(client, (loadedDraft, id, status, version, reqId) =>
            {
                draft = loadedDraft;
                activeRemoteOrderId = id;
                lastRemoteStatus = status;
                lastRemoteVersion = version;
                lastSyncedDraft = loadedDraft.Clone();
                currentRequestId = reqId;
            });
            break;
        case "q":
        case "exit":
            running = false;
            break;
        default:
            statusBanner = "Unrecognized option. Enter 1-9, k, f, n, l, or q.";
            isErrorBanner = true;
            break;
    }
}

/// <summary>
/// Prints the current order items, itemized receipt breakdown, running total, and action menu.
/// </summary>
static void RenderScreen(OrderDraft draft, PricingBreakdown pricing, MenuCatalog catalog, string apiUrl, Guid? remoteOrderId, string? remoteStatus, OrderDraft? lastSyncedDraft, bool flakySimEnabled, string? banner, bool isError)
{
    Console.WriteLine("============================================================");
    Console.WriteLine($" PIZZA BUILDER CLI                   Service: {apiUrl}");
    if (remoteOrderId.HasValue)
    {
        Console.WriteLine($" Remote Order ID: {remoteOrderId}   Status: [{remoteStatus ?? "Unknown"}]");
        if (string.Equals(remoteStatus, OrderFormatter.FormatStatus(OrderStatus.Canceled), StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(" [ORDER CANCELED & CLOSED: Press [n] to start a new order or [l] to load another order]");
            Console.ResetColor();
        }
        else if (string.Equals(remoteStatus, OrderFormatter.FormatStatus(OrderStatus.Delivered), StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" [ORDER DELIVERED & CLOSED: Press [n] to start a new order or [l] to load another order]");
            Console.ResetColor();
        }
        else if (!draft.HasSameContentAs(lastSyncedDraft))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [Local draft has unsaved changes - press [7] to push update to remote order]");
            Console.ResetColor();
        }
    }
    if (flakySimEnabled)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(" [SIMULATION ACTIVE: First POST /api/orders response will drop & auto-retry]");
        Console.ResetColor();
    }
    Console.WriteLine("------------------------------------------------------------");

    if (draft.Items.Count == 0)
    {
        Console.WriteLine(" (No pizzas in order yet)");
    }
    else
    {
        for (int i = 0; i < draft.Items.Count; i++)
        {
            var item = draft.Items[i];
            var line = pricing.Lines[i];
            var toppingsDesc = "plain";
            if (line.Toppings.Count > 0)
            {
                toppingsDesc = string.Join(", ", line.Toppings);
            }

            Console.WriteLine($" [{i + 1}] {item.Quantity}x {line.SizeName} (${line.BasePrice:0.00} base + ${line.ToppingsUnitCost:0.00} toppings = ${line.UnitPrice:0.00}/ea) -> ${line.LineTotal:0.00}");
            Console.WriteLine($"     Toppings: {toppingsDesc}");
        }
    }

    Console.WriteLine("------------------------------------------------------------");
    Console.WriteLine($" Subtotal:          ${pricing.SubtotalBeforeDiscount:0.00}");

    var promoLabel = "None";
    if (draft.PromoCodes.Count > 0)
    {
        promoLabel = string.Join(", ", draft.PromoCodes);
    }

    Console.WriteLine($" Promo ({promoLabel}):   -${pricing.DiscountAmount:0.00}");

    foreach (var warning in pricing.Warnings)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"   ! {warning}");
        Console.ResetColor();
    }

    var taxLabel = $"Tax ({catalog.TaxRate * PricingCalculator.PercentToDecimalFactor:0.#}%):";
    Console.WriteLine($" {taxLabel,-18} ${pricing.TaxAmount:0.00}");

    var tipLabel = "none";
    if (draft.Tip.Type == TipType.Percentage)
    {
        tipLabel = $"{draft.Tip.Value:0.##}% of bill";
    }
    else if (draft.Tip.Type == TipType.FixedDollars)
    {
        tipLabel = "flat $";
    }

    Console.WriteLine($" Tip ({tipLabel}):      ${pricing.TipAmount:0.00}");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($" RUNNING TOTAL:     ${pricing.GrandTotal:0.00}");
    Console.ResetColor();
    Console.WriteLine("============================================================");

    if (!string.IsNullOrWhiteSpace(banner))
    {
        if (isError)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        Console.WriteLine($" >> {banner}");
        Console.ResetColor();
        Console.WriteLine("------------------------------------------------------------");
    }

    bool isCanceled = remoteOrderId.HasValue && string.Equals(remoteStatus, OrderFormatter.FormatStatus(OrderStatus.Canceled), StringComparison.OrdinalIgnoreCase);
    bool isDelivered = remoteOrderId.HasValue && string.Equals(remoteStatus, OrderFormatter.FormatStatus(OrderStatus.Delivered), StringComparison.OrdinalIgnoreCase);
    if (isCanceled || isDelivered)
    {
        Console.WriteLine(" [6] View written order description   [8] Check remote order status");
        Console.WriteLine(" [n] Start a new order                [l] Load Order ID by GUID");
        Console.WriteLine(" [f] Toggle flaky timeout retry       [q] Quit");
        Console.WriteLine();
        return;
    }

    var submitLabel = "[7] Submit order to server";
    if (remoteOrderId.HasValue)
    {
        submitLabel = "[7] Push updates to remote order";
    }

    Console.WriteLine($" [1] Add pizza line item         [6] View written order description");
    Console.WriteLine($" [2] Modify a line item          {submitLabel}");
    Console.WriteLine($" [3] Remove a line item          [8] Check remote order status");
    Console.WriteLine($" [4] Set or clear tip            [9] Cancel remote order");
    Console.WriteLine($" [5] Apply or clear promo code   [k] Simulate Kitchen advancing status");
    Console.WriteLine($" [n] Start a new order           [l] Load Order ID by GUID");
    Console.WriteLine($" [f] Toggle flaky timeout retry  [q] Quit");
    Console.WriteLine();
}

/// <summary>
/// Prompts for size, quantity, and toppings to append a new line item to the draft.
/// </summary>
static void HandleAddLineItem(OrderDraft draft, MenuCatalog catalog, out string? msg, out bool isErr)
{
    isErr = false;
    var sizeCode = PromptSize(catalog);
    if (sizeCode == null)
    {
        msg = "Add pizza canceled.";
        return;
    }

    var qty = PromptQuantity(1);
    if (qty <= 0)
    {
        msg = "Invalid quantity. Must be a positive whole number.";
        isErr = true;
        return;
    }

    var line = new OrderLineItem
    {
        SizeCode = sizeCode,
        Quantity = qty
    };

    PromptToppingsInteractive(line, catalog);
    draft.Items.Add(line);
    msg = $"Added {line.Quantity}x {sizeCode} pizza.";
}

/// <summary>
/// Allows changing the size, quantity, or toppings of an existing 1-indexed line item.
/// </summary>
static void HandleEditLineItem(OrderDraft draft, MenuCatalog catalog, out string? msg, out bool isErr)
{
    isErr = false;
    if (draft.Items.Count == 0)
    {
        msg = "Order has no line items to edit.";
        isErr = true;
        return;
    }

    Console.Write($"Enter line item number to edit (1-{draft.Items.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out var idx) || idx < 1 || idx > draft.Items.Count)
    {
        msg = "Invalid line item number.";
        isErr = true;
        return;
    }

    var item = draft.Items[idx - 1];
    var currentToppingsText = "plain";
    if (item.Toppings.Count > 0)
    {
        currentToppingsText = string.Join(", ", item.Toppings);
    }

    Console.WriteLine($"Editing item #{idx}: {item.Quantity}x {item.SizeCode} ({currentToppingsText})");
    Console.WriteLine("  [s] Change size   [q] Change quantity   [t] Add/remove toppings   [c] Cancel");
    Console.Write("Edit choice > ");
    var sub = Console.ReadLine()?.Trim().ToLowerInvariant();

    switch (sub)
    {
        case "s":
            var newSize = PromptSize(catalog);
            if (newSize != null)
            {
                item.SizeCode = newSize;
                msg = $"Updated item #{idx} size to {newSize}.";
                return;
            }
            msg = "Size change canceled.";
            return;
        case "q":
            var newQty = PromptQuantity(item.Quantity);
            if (newQty <= 0)
            {
                msg = "Quantity must be at least 1.";
                isErr = true;
                return;
            }
            item.Quantity = newQty;
            msg = $"Updated item #{idx} quantity to {newQty}.";
            return;
        case "t":
            PromptToppingsInteractive(item, catalog);
            msg = $"Updated toppings on item #{idx}.";
            return;
        default:
            msg = "Edit canceled.";
            return;
    }
}

/// <summary>
/// Deletes a line item from the draft by its 1-based display number.
/// </summary>
static void HandleRemoveLineItem(OrderDraft draft, out string? msg, out bool isErr)
{
    isErr = false;
    if (draft.Items.Count == 0)
    {
        msg = "Order has no line items to remove.";
        isErr = true;
        return;
    }

    Console.Write($"Enter line item number to remove (1-{draft.Items.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out var idx) || idx < 1 || idx > draft.Items.Count)
    {
        msg = "Invalid line item number.";
        isErr = true;
        return;
    }

    draft.Items.RemoveAt(idx - 1);
    msg = $"Removed line item #{idx}.";
}

/// <summary>
/// Configures either a fixed dollar tip or a percentage tip on the draft.
/// </summary>
static void HandleSetTip(OrderDraft draft, out string? msg, out bool isErr)
{
    isErr = false;
    Console.WriteLine("Tip options: [1] Dollar amount ($)   [2] Percentage of total bill (%)   [0] Clear tip");
    Console.Write("Tip mode > ");
    var mode = Console.ReadLine()?.Trim();

    if (mode == "0")
    {
        draft.Tip = TipInfo.Zero;
        msg = "Tip cleared.";
        return;
    }

    if (mode != "1" && mode != "2")
    {
        msg = "Invalid tip option. Choose 1 (dollars), 2 (percentage), or 0 (clear).";
        isErr = true;
        return;
    }

    if (mode == "1")
    {
        Console.Write("Enter tip dollar amount (e.g. 4.50): $");
    }
    else
    {
        Console.Write("Enter tip percentage (e.g. 15): ");
    }

    var raw = Console.ReadLine()?.Trim().TrimStart('$').TrimEnd('%');

    if (!decimal.TryParse(raw, out var val) || val < 0m)
    {
        msg = "Invalid tip value. Please enter a non-negative number.";
        isErr = true;
        return;
    }

    if (mode == "2" && val > TipInfo.MaxPercentage)
    {
        msg = $"Tip percentage exceeds maximum allowed ({TipInfo.MaxPercentage}%).";
        isErr = true;
        return;
    }

    if (mode == "1")
    {
        draft.Tip = new TipInfo(TipType.FixedDollars, PricingCalculator.RoundCents(val));
        msg = $"Tip set to ${draft.Tip.Value:0.00}.";
    }
    else
    {
        draft.Tip = new TipInfo(TipType.Percentage, val);
        msg = $"Tip set to {draft.Tip.Value:0.##}% of the total bill.";
    }
}

/// <summary>
/// Applies or clears promo codes, validating conflicts and minimum spend before adding.
/// </summary>
static void HandlePromos(OrderDraft draft, MenuCatalog catalog, PricingCalculator calculator, out string? msg, out bool isErr)
{
    isErr = false;
    Console.WriteLine("Available promo codes:");
    foreach (var promo in catalog.Promos.Values)
    {
        var conflictNote = string.Empty;
        if (promo.ConflictsWith.Count > 0)
        {
            conflictNote = $" (cannot combine with {string.Join(", ", promo.ConflictsWith)})";
        }

        Console.WriteLine($"  - {promo.Code}: {promo.Description}{conflictNote}");
    }

    Console.WriteLine("Enter a promo code to apply, or type 'clear' to remove applied promo codes:");
    Console.Write("Promo > ");
    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(input))
    {
        msg = "Promo action canceled.";
        return;
    }

    if (string.Equals(input, "clear", StringComparison.OrdinalIgnoreCase))
    {
        draft.PromoCodes.Clear();
        msg = "Cleared all promo codes.";
        return;
    }

    if (!calculator.TryValidatePromoAddition(draft, input, out var validationMsg))
    {
        msg = validationMsg;
        isErr = true;
        return;
    }

    catalog.TryGetPromo(input, out var rule);
    draft.PromoCodes.Add(rule.Code);
    msg = validationMsg;
}

/// <summary>
/// Either submits a new order (POST /api/orders) or updates the currently attached order (PUT /api/orders/{id}).
/// Sends pricing.GrandTotal as ExpectedTotal so the server verifies client and server price parity.
/// </summary>
static async Task<(string Message, bool IsError)> HandleSubmitOrUpdateAsync(OrderDraft draft, PricingBreakdown pricing, OrderServiceClient client, string requestId, Guid? activeRemoteOrderId, int? expectedVersion, OrderDraft? lastSyncedDraft, Action<Guid, string, int, OrderDraft> onSynced)
{
    if (draft.Items.Count == 0)
    {
        return ("Cannot submit an empty order. Add at least one pizza first.", true);
    }

    if (pricing.Warnings.Count > 0)
    {
        return ($"Cannot submit order with ineligible promo: {string.Join(" ", pricing.Warnings)} Remove or satisfy the promo first.", true);
    }

    if (!activeRemoteOrderId.HasValue)
    {
        var (order, err) = await client.SubmitOrderAsync(draft, pricing.GrandTotal, requestId);
        if (err != null || order == null)
        {
            return ($"Failed to submit order: {err}", true);
        }

        onSynced(order.OrderId, order.StatusDisplay, order.Version, draft.Clone());
        return ($"Order submitted! Order ID: {order.OrderId} | Status: {order.StatusDisplay} | Charged Total: ${order.Pricing.GrandTotal:0.00}", false);
    }
    else
    {
        if (draft.HasSameContentAs(lastSyncedDraft))
        {
            return ("No local changes to push. Modify items, tip, or promos (options 1-5) before updating the remote order.", true);
        }

        var (order, err) = await client.UpdateOrderAsync(activeRemoteOrderId.Value, draft, pricing.GrandTotal, expectedVersion);
        if (err != null || order == null)
        {
            return ($"Failed to update order: {err}", true);
        }

        onSynced(order.OrderId, order.StatusDisplay, order.Version, draft.Clone());
        return ($"Order {order.OrderId} updated (v{order.Version})! Status: {order.StatusDisplay} | Total: ${order.Pricing.GrandTotal:0.00}", false);
    }
}

/// <summary>
/// Queries the Order Service for the latest status of the active order.
/// </summary>
static async Task<(string Message, bool IsError)> HandleRefreshStatusAsync(OrderServiceClient client, Guid? activeOrderId, Action<string, int> onStatusUpdated)
{
    if (!activeOrderId.HasValue)
    {
        return ("No remote order attached yet. Submit an order (option 7) or load an Order ID (option l).", true);
    }

    var (order, err) = await client.GetOrderAsync(activeOrderId.Value);
    if (err != null || order == null)
    {
        return ($"Error querying status: {err}", true);
    }

    onStatusUpdated(order.StatusDisplay, order.Version);
    return ($"Order {order.OrderId} status is '{order.StatusDisplay}' (version {order.Version}, total ${order.Pricing.GrandTotal:0.00}).", false);
}

/// <summary>
/// Attempts to cancel the active remote order on the server.
/// </summary>
static async Task<(string Message, bool IsError)> HandleCancelRemoteOrderAsync(OrderServiceClient client, Guid? activeOrderId, Action<string, int> onStatusUpdated)
{
    if (!activeOrderId.HasValue)
    {
        return ("No remote order attached to cancel.", true);
    }

    var (order, err) = await client.CancelOrderAsync(activeOrderId.Value);
    if (err != null || order == null)
    {
        return ($"Cancel rejected: {err}", true);
    }

    onStatusUpdated(order.StatusDisplay, order.Version);
    return ($"Order {order.OrderId} has been canceled.", false);
}

/// <summary>
/// Helper shortcut to trigger the kitchen status advance endpoint directly from the CLI.
/// </summary>
static async Task<(string Message, bool IsError)> HandleKitchenAdvanceAsync(OrderServiceClient client, Guid? activeOrderId, Action<string, int> onStatusUpdated)
{
    if (!activeOrderId.HasValue)
    {
        return ("Submit an order first so the kitchen has an Order ID to advance.", true);
    }

    var (order, err) = await client.AdvanceKitchenAsync(activeOrderId.Value);
    if (err != null || order == null)
    {
        return ($"Kitchen advance failed: {err}", true);
    }

    onStatusUpdated(order.StatusDisplay, order.Version);
    return ($"Kitchen advanced order {order.OrderId} -> '{order.StatusDisplay}'.", false);
}

/// <summary>
/// Immediately clears the current state and starts a fresh order draft with a new RequestId.
/// </summary>
static void StartNewOrderDraft(ref OrderDraft draft, ref string requestId, ref Guid? activeOrderId, ref string? lastRemoteStatus, ref int? lastRemoteVersion, ref OrderDraft? lastSyncedDraft, out string? msg)
{
    draft = new OrderDraft();
    requestId = Guid.NewGuid().ToString("N");
    activeOrderId = null;
    lastRemoteStatus = null;
    lastRemoteVersion = null;
    lastSyncedDraft = null;
    msg = "Started a brand-new order. Add a pizza (option 1) to begin.";
}

/// <summary>
/// Pulls an existing order down from the server by GUID so the customer can inspect or edit it.
/// </summary>
static async Task<(string Message, bool IsError)> HandleLoadOrderAsync(OrderServiceClient client, Action<OrderDraft, Guid, string, int, string> onLoaded)
{
    Console.Write("Enter Order ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var parsedId))
    {
        return ("Invalid GUID format.", true);
    }

    var (remote, err) = await client.GetOrderAsync(parsedId);
    if (err != null || remote == null)
    {
        return ($"Could not load order: {err}", true);
    }

    onLoaded(remote.Order.Clone(), remote.OrderId, remote.StatusDisplay, remote.Version, Guid.NewGuid().ToString("N"));
    return ($"Loaded order {remote.OrderId} (Status: {remote.StatusDisplay}, v{remote.Version}).", false);
}

/// <summary>
/// Prompts the user to pick a pizza size from the catalog by number or code.
/// </summary>
static string? PromptSize(MenuCatalog catalog)
{
    var sizes = catalog.Sizes.Values.ToList();
    Console.WriteLine("Available sizes:");
    for (int i = 0; i < sizes.Count; i++)
    {
        Console.WriteLine($"  [{i + 1}] {sizes[i].Name} (${sizes[i].BasePrice:0.00})");
    }

    Console.Write("Select size (number or name): ");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input))
    {
        return null;
    }

    if (int.TryParse(input, out var idx) && idx >= 1 && idx <= sizes.Count)
    {
        return sizes[idx - 1].Code;
    }

    if (catalog.TryGetSize(input, out var byName))
    {
        return byName.Code;
    }

    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Unknown size '{input}'.");
    Console.ResetColor();
    return null;
}

/// <summary>
/// Prompts for pizza quantity, defaulting to defaultQty on empty input.
/// </summary>
static int PromptQuantity(int defaultQty)
{
    Console.Write($"Enter quantity (default {defaultQty}): ");
    var raw = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return defaultQty;
    }

    if (int.TryParse(raw, out var q) && q > 0)
    {
        return q;
    }

    return -1;
}

/// <summary>
/// Interactive loop for toggling toppings on or off a line item.
/// Supports entering multiple comma-separated numbers (e.g. "1, 3, 7") to save keystrokes.
/// </summary>
static void PromptToppingsInteractive(OrderLineItem item, MenuCatalog catalog)
{
    var toppings = catalog.Toppings.Values.ToList();

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("Toggle toppings (enter number or name to add/remove, or press ENTER when done):");
        for (int i = 0; i < toppings.Count; i++)
        {
            var t = toppings[i];
            catalog.Categories.TryGetValue(t.CategoryCode, out var cat);

            var marker = "[ ]";
            if (item.Toppings.Contains(t.Code, StringComparer.OrdinalIgnoreCase))
            {
                marker = "[x]";
            }

            var pctText = t.CategoryCode;
            if (cat != null)
            {
                pctText = $"{cat.BasePricePct * PricingCalculator.PercentToDecimalFactor:0.#}% of base";
            }

            Console.WriteLine($"  {i + 1,2}. {marker} {t.Name,-20} ({cat?.Name ?? t.CategoryCode} - {pctText})");
        }

        Console.Write("Topping # (or ENTER to finish) > ");
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(input) || string.Equals(input, "done", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        // Allow comma-separated numbers like "1, 3, 7" for fast entry
        var tokens = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            ToppingDef? chosen = null;
            if (int.TryParse(token, out var tIdx) && tIdx >= 1 && tIdx <= toppings.Count)
            {
                chosen = toppings[tIdx - 1];
            }
            else if (catalog.TryGetTopping(token, out var byName, out _))
            {
                chosen = byName;
            }

            if (chosen == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Unknown topping '{token}'.");
                Console.ResetColor();
                continue;
            }

            if (item.Toppings.Contains(chosen.Code, StringComparer.OrdinalIgnoreCase))
            {
                item.RemoveTopping(chosen.Code);
                Console.WriteLine($"  - Removed {chosen.Name}");
            }
            else
            {
                item.AddTopping(chosen.Code);
                Console.WriteLine($"  + Added {chosen.Name}");
            }
        }
    }
}

static void Pause()
{
    Console.Write("Press ENTER to continue...");
    Console.ReadLine();
}
