using Microsoft.AspNetCore.Mvc;
using OrderFlow.Contracts.Dtos;
using OrderFlow.Infrastructure.Services;

namespace OrderFlow.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly OrderService _orders;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(OrderService orders, ILogger<OrdersController> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        if (request.CustomerId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(request.CustomerId), "customerId is required.");
        }

        if (request.Items.Count == 0)
        {
            ModelState.AddModelError(nameof(request.Items), "An order must contain at least one item.");
        }

        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            if (item.ProductId == Guid.Empty)
            {
                ModelState.AddModelError($"items[{i}].productId", "productId is required.");
            }

            if (item.Quantity <= 0)
            {
                ModelState.AddModelError($"items[{i}].quantity", "quantity must be greater than zero.");
            }
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var order = await _orders.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var order = await _orders.GetByIdAsync(id, ct);
        if (order is null)
        {
            _logger.LogInformation("Order {OrderId} not found", id);
            return NotFound();
        }

        return Ok(order);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            pageSize = 25;
        }

        var result = await _orders.ListAsync(page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
        => Ok(await _orders.ConfirmAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => Ok(await _orders.CancelAsync(id, ct));

    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
        => Accepted(await _orders.RetryAsync(id, ct));
}
