using Microsoft.AspNetCore.Http;
using PizzaShop.Api;
using PizzaShop.Core;
using Xunit;

namespace PizzaShop.Tests;

public class OrderLifecycleApiTests
{
    private static OrderStore CreateStore()
    {
        var catalog = MenuCatalog.CreateDefault();
        var calculator = new PricingCalculator(catalog);
        var formatter = new OrderFormatter(catalog, calculator);
        return new OrderStore(calculator, formatter);
    }

    [Fact]
    public async Task OrderLifecycle_ProgressesThroughAllKitchenStates_AndBlocksCustomerEditsAfterStarted()
    {
        var store = CreateStore();

        var createReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("medium", 1, new List<string> { "pepperoni" }) }, new List<string>(), new TipDto(TipType.FixedDollars, 2.00m));

        var createRes = await store.CreateOrderAsync(createReq);
        Assert.Equal(StatusCodes.Status201Created, createRes.HttpStatus);
        Assert.True(createRes.CreatedNew);
        Assert.Equal(OrderStatus.NotStarted, createRes.Response!.Status);

        var orderId = createRes.Response.OrderId;

        // Customer can update the order while it is still NotStarted
        var updateReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 1, new List<string> { "pepperoni", "bacon" }) }, new List<string> { "ILOVEPIZZA" }, new TipDto(TipType.FixedDollars, 2.00m), null, 1);

        var updateRes = await store.UpdateOrderAsync(orderId, updateReq);
        Assert.Equal(StatusCodes.Status200OK, updateRes.HttpStatus);
        Assert.Equal(2, updateRes.Response!.Version);

        // Stale ExpectedVersion is rejected with 409 Conflict
        var staleUpdate = await store.UpdateOrderAsync(orderId, updateReq);
        Assert.Equal(StatusCodes.Status409Conflict, staleUpdate.HttpStatus);

        // Kitchen advances: NotStarted -> Started
        var adv1 = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(StatusCodes.Status200OK, adv1.HttpStatus);
        Assert.Equal(OrderStatus.Started, adv1.Response!.Status);

        // Customer can no longer update or cancel once Started
        var lateUpdateReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 1, new List<string> { "pepperoni", "bacon" }) }, new List<string> { "ILOVEPIZZA" }, new TipDto(TipType.FixedDollars, 2.00m));
        var lateUpdate = await store.UpdateOrderAsync(orderId, lateUpdateReq);
        Assert.Equal(StatusCodes.Status409Conflict, lateUpdate.HttpStatus);

        var lateCancel = await store.CancelOrderAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, lateCancel.HttpStatus);

        // Kitchen advances: Started -> ReadyForPickup -> Delivered
        var adv2 = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(OrderStatus.ReadyForPickup, adv2.Response!.Status);

        var adv3 = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(OrderStatus.Delivered, adv3.Response!.Status);

        // Advancing past Delivered returns 409 Conflict
        var adv4 = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, adv4.HttpStatus);
    }

    [Fact]
    public async Task RetryingSubmissionWithSameRequestId_ReturnsSameOrderWithoutDuplicating()
    {
        var store = CreateStore();
        var requestId = Guid.NewGuid().ToString("N");

        var req = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 2, new List<string> { "onions" }) }, null, null, null, null, requestId);

        var firstAttempt = await store.CreateOrderAsync(req);
        Assert.Equal(StatusCodes.Status201Created, firstAttempt.HttpStatus);
        Assert.True(firstAttempt.CreatedNew);

        // Simulate client retrying after a dropped connection using the same RequestId
        var retryAttempt = await store.CreateOrderAsync(req);
        Assert.Equal(StatusCodes.Status200OK, retryAttempt.HttpStatus);
        Assert.False(retryAttempt.CreatedNew);

        Assert.Equal(firstAttempt.Response!.OrderId, retryAttempt.Response!.OrderId);
        Assert.Single(await store.ListOrdersAsync());
    }

    [Fact]
    public async Task ServerRejectsOrder_WhenClientExpectedTotalDoesNotMatchServerCalculation()
    {
        var store = CreateStore();

        // 1 small plain pizza is $5.00 + $0.33 tax = $5.33, but client claims $1.00
        var tamperedReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, new List<string>()) }, null, null, 1.00m);

        var res = await store.CreateOrderAsync(tamperedReq);
        Assert.Equal(StatusCodes.Status400BadRequest, res.HttpStatus);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public async Task ConcurrentCancelAndKitchenStart_GuaranteesMutualExclusion()
    {
        var store = CreateStore();

        // Run 25 concurrent races between customer cancel and kitchen start
        for (int i = 0; i < 25; i++)
        {
            var created = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("medium", 1, new List<string> { "pepperoni" }) }));
            var orderId = created.Response!.OrderId;

            var cancelTask = Task.Run(() => store.CancelOrderAsync(orderId));
            var kitchenStartTask = Task.Run(() => store.AdvanceStatusAsync(orderId));

            await Task.WhenAll(cancelTask, kitchenStartTask);

            var cancelStatus = cancelTask.Result.HttpStatus;
            var kitchenStatus = kitchenStartTask.Result.HttpStatus;

            // Exactly one must win (200 OK) and the other must be rejected (409 Conflict)
            bool cancelWon = cancelStatus == StatusCodes.Status200OK && kitchenStatus == StatusCodes.Status409Conflict;
            bool kitchenWon = kitchenStatus == StatusCodes.Status200OK && cancelStatus == StatusCodes.Status409Conflict;

            Assert.True(cancelWon || kitchenWon, $"Expected mutually exclusive outcome, but got Cancel={cancelStatus}, Kitchen={kitchenStatus}");

            var finalOrder = await store.GetOrderAsync(orderId);
            if (cancelWon)
            {
                Assert.Equal(OrderStatus.Canceled, finalOrder!.Status);
            }
            else
            {
                Assert.Equal(OrderStatus.Started, finalOrder!.Status);
            }
        }
    }

    [Fact]
    public async Task EmptyOrderOrIneligiblePromo_IsRejectedWith400BadRequest()
    {
        var store = CreateStore();

        // Empty items list
        var emptyRes = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto>()));
        Assert.Equal(StatusCodes.Status400BadRequest, emptyRes.HttpStatus);
        Assert.Equal("EMPTY_ORDER", emptyRes.Error!.ErrorCode);

        // SAVE5 on a $5.00 order (below the > $20.00 threshold)
        var ineligiblePromoReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, new List<string>()) }, new List<string> { "SAVE5" });

        var promoRes = await store.CreateOrderAsync(ineligiblePromoReq);
        Assert.Equal(StatusCodes.Status400BadRequest, promoRes.HttpStatus);
        Assert.Equal("INELIGIBLE_PROMO", promoRes.Error!.ErrorCode);
    }

    [Fact]
    public async Task OperationsOnUnknownOrderId_Return404NotFound()
    {
        var store = CreateStore();
        var missingId = Guid.NewGuid();

        Assert.Null(await store.GetOrderAsync(missingId));

        var updateRes = await store.UpdateOrderAsync(missingId, new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, null) }));
        Assert.Equal(StatusCodes.Status404NotFound, updateRes.HttpStatus);

        var cancelRes = await store.CancelOrderAsync(missingId);
        Assert.Equal(StatusCodes.Status404NotFound, cancelRes.HttpStatus);

        var advanceRes = await store.AdvanceStatusAsync(missingId);
        Assert.Equal(StatusCodes.Status404NotFound, advanceRes.HttpStatus);
    }

    [Fact]
    public async Task CanceledOrder_RejectsSubsequentCancelUpdateAndKitchenAdvanceWith409Conflict()
    {
        var store = CreateStore();

        var created = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("medium", 1, null) }));
        var orderId = created.Response!.OrderId;

        var firstCancel = await store.CancelOrderAsync(orderId);
        Assert.Equal(StatusCodes.Status200OK, firstCancel.HttpStatus);
        Assert.Equal(OrderStatus.Canceled, firstCancel.Response!.Status);

        // Once canceled, the order is closed: a second cancel, an update, or a kitchen advance all return 409 Conflict
        var secondCancel = await store.CancelOrderAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, secondCancel.HttpStatus);
        Assert.Equal("ORDER_ALREADY_CANCELED", secondCancel.Error!.ErrorCode);

        var updateAfterCancel = await store.UpdateOrderAsync(orderId, new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 1, null) }));
        Assert.Equal(StatusCodes.Status409Conflict, updateAfterCancel.HttpStatus);

        var kitchenAdvance = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, kitchenAdvance.HttpStatus);
    }

    [Fact]
    public async Task StoredOrder_IsIsolatedFromCallerMutatingReturnedDraft()
    {
        var store = CreateStore();

        var created = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, new List<string> { "pepperoni" }) }));

        // Mutate the returned OrderDraft in memory outside the store
        created.Response!.Order.Items.Clear();

        // Stored order inside OrderStore should remain untouched
        var fetched = await store.GetOrderAsync(created.Response.OrderId);
        Assert.NotNull(fetched);
        Assert.Single(fetched.Order.Items);
        Assert.Equal("small", fetched.Order.Items[0].SizeCode);
    }

    [Fact]
    public async Task AdvanceAllActiveOrdersOneStep_ProgressesActiveOrdersAndSkipsTerminalOnes()
    {
        var store = CreateStore();

        var activeOrder = (await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, null) }))).Response!;
        var canceledOrder = (await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 1, null) }))).Response!;
        await store.CancelOrderAsync(canceledOrder.OrderId);

        var count = await store.AdvanceAllActiveOrdersOneStepAsync();
        Assert.Equal(1, count);

        Assert.Equal(OrderStatus.Started, (await store.GetOrderAsync(activeOrder.OrderId))!.Status);
        Assert.Equal(OrderStatus.Canceled, (await store.GetOrderAsync(canceledOrder.OrderId))!.Status);
    }

    [Fact]
    public async Task ConcurrentDuplicateRetries_ReturnSameOrderWithoutDuplicating()
    {
        var store = CreateStore();
        var requestId = Guid.NewGuid().ToString("N");
        var req = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("medium", 1, new List<string> { "pepperoni" }) }, null, null, null, null, requestId);

        var task1 = Task.Run(() => store.CreateOrderAsync(req));
        var task2 = Task.Run(() => store.CreateOrderAsync(req));

        await Task.WhenAll(task1, task2);

        var res1 = task1.Result;
        var res2 = task2.Result;

        Assert.Equal(res1.Response!.OrderId, res2.Response!.OrderId);
        Assert.True((res1.CreatedNew && !res2.CreatedNew) || (!res1.CreatedNew && res2.CreatedNew));
        Assert.Single(await store.ListOrdersAsync());
    }

    [Fact]
    public async Task DeliveredOrder_RejectsCancelUpdateAndKitchenAdvanceWith409Conflict()
    {
        var store = CreateStore();

        var created = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, null) }));
        var orderId = created.Response!.OrderId;

        // Step to Delivered: NotStarted -> Started -> ReadyForPickup -> Delivered
        await store.AdvanceStatusAsync(orderId);
        await store.AdvanceStatusAsync(orderId);
        var delivered = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(OrderStatus.Delivered, delivered.Response!.Status);

        // All mutations on a Delivered order must be rejected with 409 Conflict
        var cancelRes = await store.CancelOrderAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, cancelRes.HttpStatus);

        var updateRes = await store.UpdateOrderAsync(orderId, new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 1, null) }));
        Assert.Equal(StatusCodes.Status409Conflict, updateRes.HttpStatus);

        var advanceRes = await store.AdvanceStatusAsync(orderId);
        Assert.Equal(StatusCodes.Status409Conflict, advanceRes.HttpStatus);
        Assert.Equal("ORDER_ALREADY_DELIVERED", advanceRes.Error!.ErrorCode);
    }

    [Fact]
    public async Task RejectedUpdate_LeavesStoredOrderUnmodified()
    {
        var store = CreateStore();

        var created = await store.CreateOrderAsync(new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("small", 1, new List<string> { "pepperoni" }) }));
        var orderId = created.Response!.OrderId;

        // Attempt update with mismatched expectedTotal ($99.99)
        var invalidUpdateReq = new SubmitOrderRequest(new List<OrderLineItemDto> { new OrderLineItemDto("large", 2, new List<string> { "bacon" }) }, null, null, 99.99m, 1);

        var updateRes = await store.UpdateOrderAsync(orderId, invalidUpdateReq);
        Assert.Equal(StatusCodes.Status400BadRequest, updateRes.HttpStatus);
        Assert.Equal("PRICE_MISMATCH", updateRes.Error!.ErrorCode);

        // Verify stored order remains untouched at Version 1 with original item
        var stored = await store.GetOrderAsync(orderId);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
        Assert.Single(stored.Order.Items);
        Assert.Equal("small", stored.Order.Items[0].SizeCode);
    }
}
