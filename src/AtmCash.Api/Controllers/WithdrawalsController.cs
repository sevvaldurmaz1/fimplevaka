using System.ComponentModel.DataAnnotations;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Cash;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

public sealed record QuoteWithdrawalRequest(
    [Required, RegularExpression("^[0-9]{13,19}$", ErrorMessage = "Card number must contain 13–19 digits.")] string CardNumber,
    [Required] Currency? Currency,
    [Range(1, 20_000)] int Amount);
public sealed record ConfirmWithdrawalRequest([Range(1, 20_000)] int Amount);

[ApiController]
[Route("api/atms/{atmId:guid}/withdrawals/quotes")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status410Gone)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
public sealed class WithdrawalsController(WithdrawalService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<QuoteView>(StatusCodes.Status201Created)]
    public async Task<ActionResult<QuoteView>> Quote(Guid atmId, QuoteWithdrawalRequest request, CancellationToken cancellationToken)
    {
        var quote = await service.QuoteAsync(atmId, request.CardNumber, request.Currency!.Value, request.Amount, cancellationToken);
        return CreatedAtAction(nameof(Get), new { atmId, quoteId = quote.Id }, quote);
    }

    [HttpGet("{quoteId:guid}")]
    [ProducesResponseType<QuoteView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<QuoteView>> Get(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
        => Ok(await service.GetAsync(atmId, quoteId, cancellationToken));

    [HttpPost("{quoteId:guid}/confirm")]
    [ProducesResponseType<ConfirmationView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConfirmationView>> Confirm(Guid atmId, Guid quoteId, ConfirmWithdrawalRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(128)] string idempotencyKey,
        CancellationToken cancellationToken)
        => Ok(await service.ConfirmAsync(atmId, quoteId, request.Amount, idempotencyKey, cancellationToken));

    [HttpDelete("{quoteId:guid}")]
    [ProducesResponseType<QuoteView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<QuoteView>> Cancel(Guid atmId, Guid quoteId, CancellationToken cancellationToken)
        => Ok(await service.CancelAsync(atmId, quoteId, cancellationToken));
}
