# PizzaShop
This repo contains the pizza order builder CLI (`PizzaShop.Cli`), the remote order service (`PizzaShop.Api`), and a shared core library (`PizzaShop.Core`) built on .NET 8.

## Running Locally
You'll need the .NET 8 SDK installed.

**1. Start the Order Service**
Open a terminal at the repo root and run:
`dotnet run --project src/PizzaShop.Api`

Or for a modified log level such as `Debug`: `dotnet run --project src/PizzaShop.Api -- --Logging:LogLevel:Default=Debug`

By default this starts the API on `http://localhost:5080`.

For the kitchen side, you can either advance orders manually (via the API or directly from the CLI menu), or have the server automatically step active orders forward on a timer by setting `KITCHEN_AUTO_ADVANCE_SECONDS`:
- `$env:KITCHEN_AUTO_ADVANCE_SECONDS=20; dotnet run --project src/PizzaShop.Api` -- Uses timer to advance order.
- `$env:KITCHEN_AUTO_ADVANCE_SECONDS=0; dotnet run --project src/PizzaShop.Api` -- Does not use timer to advance order.

**2. Start the Order Builder CLI**
In a second terminal, run:
`dotnet run --project src/PizzaShop.Cli`

As you add pizzas, toppings, promos, or a tip, the running total at the top of the screen updates after each step. I also included two helper shortcuts right in the CLI menu (`k` to step the kitchen status forward, and `f` to simulate a dropped mobile connection and retry) to make testing easier.

## CLI Usage & Walkthrough
The CLI shows a live summary at the top of the screen (items, subtotal, promo discount, 6.5% tax, tip, and grand total) that recalculates after every action.

### Quick Walkthroughs to Try

1. **Submit and update an order while `Not Started`:**
   - Use `[1]` to add a Large pizza with Pepperoni, `[5]` to apply `ILOVEPIZZA`, and `[4]` to add a tip.
   - Press `[7]` to submit. Note the attached Order ID and `Status: [Not Started]` in the header.
   - Use `[1]`, `[2]`, or `[3]` to modify the cart (the banner will note `[Local draft has unsaved changes - press [7] to push update to remote order]`), then press `[7]` again to push the update (`v2`). If you press `[7]` without making any local changes, the CLI detects that nothing changed and skips the network call.
2. **Kitchen progression and `409 Conflict` lock-out:**
   - With a submitted order, press `[k]` to advance the kitchen status from `Not Started` to `Started`.
   - Try modifying the cart and pressing `[7]` (push update) or `[9]` (cancel). The server will reject the request with `409 Conflict` because the kitchen has already started preparing the order.
   - Press `[k]` twice more to step through `Ready for Pickup` and `Delivered`. Once `Delivered`, the order is complete and the CLI locks it into read-only mode.
3. **Cancel an order and verify read-only lock:**
   - Press `[n]` to start a new order, add a pizza with `[1]`, and submit with `[7]`.
   - While status is `Not Started`, press `[9]` to cancel. The order transitions to `Canceled` and the CLI collapses the menu to read-only/navigation options.
4. **Simulated mobile timeout & retry deduplication:**
   - Press `[n]` to start a new order, add a pizza with `[1]`, and press `[f]` to turn timeout simulation `ON`.
   - Press `[7]` to submit. The client will simulate a network timeout after the first request and retry with the same `RequestId` without creating a duplicate order on the server.

### Menu Options

- **Building the Order (`[1]`–`[6]`):**
  - `[1]` **Add pizza line item:** Pick a size (`Small`, `Medium`, `Large`), enter a quantity, and toggle toppings by number or code.
  - `[2]` **Modify a line item:** Change size, quantity, or toppings on an existing line item.
  - `[3]` **Remove a line item:** Delete a pizza line item from the order.
  - `[4]` **Set or clear tip:** Choose a fixed dollar tip, a percentage tip of the total bill, or clear the tip.
  - `[5]` **Apply or clear promo code:** Enter `ILOVEPIZZA` (10% off pre-tax), `SAVE5` ($5 off pre-tax orders over $20), or `clear` to remove applied promos. Conflicting promos are rejected immediately.
  - `[6]` **View written order description:** Prints the full receipt breakdown and any promo eligibility notes.
- **Remote Order Lifecycle (`[7]`–`[9]`, `[n]`, `[l]`):**
  - `[7]` **Submit / push update to remote Order Service:** First press submits the order (`POST /api/orders`) and attaches the session to the returned Order ID (`Not Started`). If you edit the order locally while it is still `Not Started`, pressing `[7]` again pushes the update (`PUT /api/orders/{id}`). If there are no local changes, the CLI lets you know and skips the network call.
  - `[8]` **Check remote order status:** Fetches the latest status, version, and total from the server (`GET /api/orders/{id}`).
  - `[9]` **Cancel remote order:** Cancels the attached order (`POST /api/orders/{id}/cancel`) if it is still `Not Started`. Once canceled, the CLI locks that order into read-only mode and hides the edit/mutation options.
  - `[n]` **Start a fresh new order:** Detaches from the current remote order and resets the local cart so you can submit another order.
  - `[l]` **Load Order ID by GUID:** Pulls an existing order from the server (`GET /api/orders/{id}`) into the CLI session so you can inspect or edit it.
- **Test & Simulation Shortcuts (`[k]`, `[f]`):**
  - `[k]` **Advance kitchen status (simulator):** Steps the attached order forward one stage (`Not Started` -> `Started` -> `Ready for Pickup` -> `Delivered`) via `POST /api/orders/{id}/advance` without needing a separate `curl` terminal. Once an order reaches `Delivered` (or `Canceled`), the CLI collapses the menu to read-only/navigation options.
  - `[f]` **Toggle simulated mobile timeout retry:** When `ON`, the next `[7]` submission simulates a dropped connection on the first attempt and automatically retries with the same `RequestId` so you can verify retry deduplication.

## Order Service API
The service exposes a small JSON API:

- `GET /api/menu` - Returns the configured pizza sizes, topping categories, toppings, promo rules, and tax rate.
- `GET /api/orders` - Lists submitted orders (newest first).
- `POST /api/orders` - Submits a new order (starts as Not Started). Accepts a `requestId` in the body so client retries don't create duplicate orders.
- `GET /api/orders/{id}` - Gets the current status (Not Started, Started, Ready for Pickup, Delivered, or Canceled), order description, and itemized price breakdown.
- `PUT /api/orders/{id}` - Updates items, promos, or tip on an order, as long as it is still Not Started.
- `POST /api/orders/{id}/cancel` - Cancels an order if it is still Not Started.
- `POST /api/orders/{id}/advance` - Advances an order one step through the kitchen lifecycle (Not Started -> Started -> Ready for Pickup -> Delivered).

Example request to create an order:
```powershell
curl.exe -X POST http://localhost:5080/api/orders -H "Content-Type: application/json" -d '{"items":[{"sizeCode":"large","quantity":2,"toppings":["pepperoni","bacon"]}],"promoCodes":["SAVE5"],"tip":{"type":"Percentage","value":15},"expectedTotal":28.85,"requestId":"9b2d8e1a-4c3f-4a10-8e2b-112233445566"}'
```

Advancing or canceling an order:
```powershell
curl.exe -X POST http://localhost:5080/api/orders/{id}/advance
curl.exe -X POST http://localhost:5080/api/orders/{id}/cancel
```

## Assumptions
There were a few spots where the pricing and lifecycle rules could be interpreted in more than one way. Here is what I went with:

- Tax is calculated after promo discounts. Both promo rules say they apply to the "total order before tax", so discounts come off the subtotal first, and the 6.5% tax is applied to the discounted subtotal.

- For SAVE5, "over $20" means strictly greater than $20.00. A pre-tax subtotal of $20.00 flat doesn't get the $5 discount; $20.01 does.

- If a customer applies SAVE5 when their order is $24.00 and then removes a pizza so the subtotal drops to $12.00, the calculator doesn't crash or block the edit. Instead, it drops the discount to $0.00 and shows a note explaining that SAVE5 requires a subtotal over $20.00. If they add another item later, the discount kicks back in.

- Percentage tips are based on the post-tax bill (discounted subtotal + tax). I noticed the prompt used "total order before tax" for promos, "total order" for tax, and "percentage of the total bill" for tips, so treating the "total bill" as the post-tax amount felt most natural. Tips themselves aren't taxed.

- Every line on the receipt (topping cost, line item total, discount, tax, and tip) is rounded to whole cents using. Rounding at each line item stage also avoids the annoying issue where displayed lines appear to be off by a penny compared to the bottom-line total.

- Toppings on a single line item are on/off (either on the pizza or not) rather than having Lite / Normal / Extra portion multipliers. Since "extra cheese" is explicitly listed as its own basic topping in the prompt and there are no pricing rules for partial or double toppings, adding the same topping twice to a single line item is treated as a no-op. Multiple identical pizzas are handled via the line item quantity.

- When an order is canceled, I keep the record around with a `Canceled` status instead of deleting it, so querying the order ID later still gives a clear answer rather than a 404.

## Design Write-Up

### Money & Pricing Logic
The currency calculation logic lives in `PricingCalculator` inside `PizzaShop.Core`, which runs in both processes. The CLI evaluates it locally for instantaneous running-total feedback, while the API runs it as the authoritative validator on every order submit or update. The client includes its running total as `expectedTotal` in the payload, and the server rejects any submission where the cleint's expectation does not match the server's calculation.

**Caveat on production service architecture:** In a real-world system (where the client is typically a web frontend or a mobile app, and menu prices or promo rules change dynamically in a backend database), pricing logic and promo rules would **only** live in the backend service rather than being compiled into the client. Shipping pricing and promo catalogs inside client code has two big downsides: first, stale clients will calculate outdated totals whenever the restaurant changes a price. And second, any promo codes or internal pricing rules baked into a client code can be easily decompiled by users to discover unadvertised promo codes. In production, the client would hit a lightweight quote or cart-update endpoint to get the server-calculated running total and itemized breakdown, and pass that quote total back as `expectedTotal` on final submission so the server can still verify the price didn't change between preview and checkout.

### Extensibility (New Sizes, Categories, Toppings, and Promos)
A common trap here is modeling pizza sizes or topping categories as C# enums with a `switch` statement in the pricing calculator. If you do that, adding an XL size or a new topping tier means editing core pricing code and recompiling. Instead, we load sizes, topping categories (with their base price percentage), toppings, and promo rules from `menu.json` on startup (with a in-code fallback if the file isn't found) and store them in case-insensitive dictionaries. `PricingCalculator` never checks for "small" or "basic" by name; it just looks up the size's base price and multiplies it by each topping's category percentage. Adding a new size, topping tier, or topping is just editing `menu.json`.

Promos work similarly through the `IPromoRule` interface. Each promo rule defines its own `IsEligible` check, its `CalculateDiscount` math, and a `ConflictsWith` collection of promo codes it can't be combined with. Standard percentage and minimum-spend promos can be added directly in `menu.json` (including their spending threshold and `conflictsWith` list), and custom rule types only require implementing `IPromoRule` and registering it on the catalog.

### Order Lifecycle, Retries, and Concurrency
Orders start in `NotStarted` and move through `Started` -> `ReadyForPickup` -> `Delivered` as the kitchen advances them. Customer updates and cancellations are only allowed while the status is `NotStarted`.

**Retries after a mobile timeout**: when a customer starts building an order in the CLI, the CLI generates a unique `requestId` GUID and includes it on the create POST payload. If the server creates the order, but the customer's connection drops before the response makes it back, our client catches the timeout and retries the POST with the same `requestId`. On the server our datastore checks its dictionary inside the semaphore, sees that ID already created an order, and returns the existing order with Http 200 instead of creating a second one.

**A customer trying to cancel at the exact moment the kitchen starts the order**: all state checks and transitions in `OrderStore` are guarded by a mutual-exclusion semaphore. If the customer's cancel request and the kitchen's advance request hit the server at the same millisecond, one of them enters the semaphore first:

- If the kitchen enters the semaphore first, the status changes from `NotStarted` to `Started` and version increments to 2. When the customer's cancel request enters right after, it sees the status is now `Started` and returns `409 Conflict`.

- If the customer's cancel enters the semaphore first, the status changes to `Canceled`. When the kitchen's advance request enters right after, it sees the order is `Canceled` and returns `409 Conflict`.

Because both transitions check the current state and mutate it inside the same critical section, we guarantee the customer is never told their order was canceled while the kitchen starts cooking it.

### Defensive Programming, Non-Happy Paths, and Debuggability
In my day-to-day work and when reviewing other engineers' PRs, defensive coding and operational debuggability are two things I care about a lot and consistently look for -- mainly because it's been worked into us through trial and error when supporting escalated incidents from the DevOps team. Happy-path code is easy to write, but when something goes sideways in a real environment, the system needs both a safe fallback recourse and enough context in the logs to diagnose what happened without attaching a debugger.

Even within the timebox, I made sure those habits were reflected across the projects:

- **Structured logging on both happy and non-happy paths (`OrderStore` & `PizzaShop.Api`):** Logs structured context on every state transition and logs a `Warning` with the competing values whenever a request is rejected (price mismatch, version conflict, or losing the race against the kitchen).

- **Graceful startup fallback (`MenuCatalog`):** If `menu.json` is missing or malformed on startup, a warning is logged and falls back to an in-code default instead of crashing the process.

- **State isolation & resilient network calls (`OrderStore` & `OrderServiceClient`):** `OrderStore` deep-clones `OrderDraft` instances (`ICloneable<T>`) going in and out of storage so callers can't mutate server state, and our order service client catches network/timeout errors so a dropped connection surfaces a clean message instead of crashing the CLI.

### What I Cut for the Timebox & What I'd Build Next
To keep this within the 3-hour window, I focused on getting the pricing math, extensibility, and order state transitions solid, and took a few deliberate shortcuts that I'd address next in a real production service:

- **Controllers & OpenAPI/Swagger documentation instead of Minimal API:** To move fast within the timebox, the endpoints in `PizzaShop.Api -> Program.cs` use ASP.NET Minimal APIs and skipped OpenAPI/Swagger setup since it wasn't required by the prompt. In a normal production service, I'd implement these as full Controllers (`OrdersController`, `MenuController`) with proper attributes and OpenAPI/Swagger documentation.

- **Database persistence & optimistic concurrency (for multi-replica scaling):** Right now, orders and request IDs live in memory inside `OrderStore` guarded by a single process-wide Semaphore Slim. That's simple, async-friendly, and thread-safe for a single process, but everything resets on restart and an in-memory lock wouldn't work once the API is scaled horizontally across multiple pods in Kubernetes. Next, I'd move storage to a database layer to use database-level optimistic concurrency, meaning a non-locking approach to atomicity using a `Version` column in the `WHERE` clause so any replica can safely handle requests without sticky sessions or in-process locks.

- **Request payload hashing & TTL expiration on `RequestId`:** Right now our in-memory datastore just maps `RequestId -> OrderId` in memory forever. In production, I'd also store a hash of the request payload alongside the ID (so if a client accidentally reuses the same `RequestId` for two different orders, the server rejects it instead of returning the first order) and add a 24-hour TTL so old keys get cleaned up.

- **Event-driven kitchen pipeline & real-time status push:** Right now, kitchen progression is simulated in-process, and the CLI polls status on demand. In production, order intake and kitchen fulfillment would be decoupled through an event stream or message bus (such as AWS SNS/SQS or Kafka) -- submitting or canceling an order would publish an event for the kitchen system to consume, and kitchen status transitions (`Started`, `ReadyForPickup`, `Delivered`) would publish events back out to push live updates to the client over SSE or SignalR.

- **Topping portion modifiers (Lite / Regular / Extra):** If the restaurant wanted portion sizing beyond the flat "extra cheese" topping, we could store `(ToppingCode, Portion)` on `OrderLineItem` and map each portion to a multiplier in `MenuCatalog`.

### Code Review Nits I'd Clean Up Before a Real PR
If I were reviewing this PR myself outside of a 3-hour timebox, here are the code-level nits I'd flag for cleanup:

- **Messy variable and lambda usage in `PizzaShop.Cli -> Program.cs`:** As I wired up remote order tracking and status banners, `Program.cs` accumulated several local session variables passed around via `ref`/`out` and `Action<...>` callback lambdas. Wrapping those into a single `CliSessionState` class would clean up all of those method signatures.

- **4-element value tuple return types on `OrderStore`:** Methods like `CreateOrderAsync` return a larger tuple. Tuples were quick to use, but a dedicated `StoreResult<T>` record is much cleaner.

- **`OrderStore` returning HTTP status integers:** `OrderStore` directly references `409` / `400` instead of returning a domain-level result enum and letting the HTTP endpoints map those to status codes.

- **`new HttpClient()` inside `OrderServiceClient`:** News up its own `HttpClient` directly (and isn't `IDisposable`) instead of taking an injected `HttpClient` or via factory. Fine for a single long-lived CLI instance, but a bad habit in server code due to socket exhaustion.

- **Duplicated client DTOs & anonymous request payload:** `PizzaShop.Cli` defines its own order and error DTO and builds an anonymous object rather than sharing the request/response contract records in a common Contracts library.

- **Linear scans with `.Any(...)`:** `OrderLineItem -> Toppings` uses a list checked with `.Any(...)` for easy JSON serialization, whereas a case-insensitive `HashSet<string>` would avoid repeated linear scans.

- **XML docs without `<param>` tags:** I wrote `<summary>`, `<returns>`, and `<exception>` blocks alongside inline comments explaining the intent on classes and methods, but skipped individual `<param>` tags to save time.

## Testing
Run the test suite with:
`dotnet test`

The test suite in `src/tests/PizzaShop.Tests` focuses on domain invariants, financial rounding, and concurrency rather than just happy paths:
- **`PricingTests.cs`:** 8% and 11% topping math, `AwayFromZero` midpoint rounding, `SAVE5` strict $20 boundary and cart shrinkage, promo conflicts, tip calculations, and draft change detection.
- **`ExtensibilityTests.cs`:** Registering new sizes, topping tiers, toppings, and custom promo rules without modifying pricing code, plus `menu.json` fallback on missing/malformed files.
- **`OrderLifecycleApiTests.cs`:** Status progressions (`NotStarted` -> `Delivered`), `409 Conflict` guards on started/canceled/delivered orders, `RequestId` retry deduplication (including concurrent retries), state isolation via defensive cloning, and a 25-iteration race test between customer cancel and kitchen start.

**What was cut for the timebox:** 
- Automated stdin/stdout console tests for `PizzaShop.Cli` (verified manually).
- Raw TCP socket-drop tests over Kestrel (tested via the simulated mobile timeout flag).

## AI Usage Note
I used Gemini (via Antigravity / AGY CLI) during this exercise to knock out the tedious parts. Mostly drafting the console menus, DTOs, endpoint boilerplate, and assisting in unit testing.

Where I had to correct it or take over:
- It initially tried to hardcode pizza sizes and toppings as C# enums with switch statements. I changed that to load from `menu.json` so you can add new sizes or toppings without editing code and recompiling.
- It set a 5-second HTTP timeout on the client with auto-retries. When I paused at a breakpoint in the API to debug, the CLI would time out and start firing duplicate retry requests, so I turned off the timeout for local debugging.
- The scaffolded CLI menu allowed editing or canceling orders even after they were delivered or canceled, and sent update requests when the cart hadn't changed. I locked down the menu for finalized orders and added a check to skip updates if nothing was modified.
- It hallucinated the wrong total in the `curl` example ($30.89 instead of $28.85), which would have failed the server's price check.

AI only knows how to test what it reads in the implementation; it lacks the underlying business intent unless you give it context. Early on, it only generated happy-path tests that mirrored the code rather than testing boundary intent (like $20.00 flat failing `SAVE5` while $20.01 passes, or retail rounding vs .NET's default banker's rounding). That's why I write clear XML summaries and inline comments explaining *why* something is done -- good in-code documentation helps both human reviewers and AI tools understand the intent, though a developer still has to step in and design the non-obvious edge cases and race conditions by hand.
