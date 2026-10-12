using System.ComponentModel.DataAnnotations;
using AtmCash.Application.Deposits;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

public sealed record DepositNoteRequest([Required] Currency? Currency, int Denomination, [Range(1, int.MaxValue)] int Count);
public sealed record DepositRequest(
    [Required, MaxLength(DepositResult.MaximumNoteGroups)] DepositNoteRequest[] Notes,
    [Required, Range(0, int.MaxValue)] int? UnrecognizedCount);

[ApiController]
[Route("api/atms/{atmId:guid}/deposits")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
public sealed class DepositsController(DepositService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<DepositView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DepositView>> Deposit(Guid atmId, DepositRequest request, CancellationToken cancellationToken)
    {
        if (request.Notes.Any(note => note is null))
            throw new DomainException("invalid_deposit_notes", "Note entries cannot be null.");
        var notes = request.Notes.Select(note => new NoteBatch(note.Currency!.Value, note.Denomination, note.Count)).ToArray();
        return Ok(await service.DepositAsync(atmId, notes, request.UnrecognizedCount!.Value, cancellationToken));
    }
}
