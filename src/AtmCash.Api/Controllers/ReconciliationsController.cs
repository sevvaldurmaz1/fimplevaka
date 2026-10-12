using System.ComponentModel.DataAnnotations;
using AtmCash.Application.Operations;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Reconciliation;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

public sealed record CountedNoteRequest([Required] Currency? Currency, int Denomination, [Required, Range(0, 100)] int? Count);
public sealed record CountedCassetteRequest(int Number, [Required, MaxLength(12)] CountedNoteRequest[] Notes);
public sealed record ReconcileRequest([Required, MinLength(5), MaxLength(5)] CountedCassetteRequest[] Cassettes);

[ApiController]
[Route("api/atms/{atmId:guid}/reconciliations")]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType<ProblemDetails>(404)]
[ProducesResponseType<ProblemDetails>(409)]
[ProducesResponseType<ProblemDetails>(422)]
public sealed class ReconciliationsController(ReconciliationService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ReconciliationView>(201)]
    public async Task<ActionResult<ReconciliationView>> Submit(Guid atmId, ReconcileRequest request,
        [FromHeader(Name = "X-User"), Required, StringLength(100)] string user, CancellationToken token)
    {
        if (request.Cassettes.Any(cassette => cassette is null || cassette.Notes.Any(note => note is null)))
            throw new DomainException("invalid_reconciliation_counts", "Cassette and banknote entries cannot be null.");
        var counts = request.Cassettes.Select(cassette => new CassetteCount(cassette.Number,
            cassette.Notes.Select(note => new NoteBatch(note.Currency!.Value, note.Denomination, note.Count!.Value)).ToArray())).ToArray();
        var result = await service.SubmitAsync(atmId, counts, user, token);
        return CreatedAtAction(nameof(Get), new { atmId, id = result.Id }, result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReconciliationView>(200)]
    public async Task<ActionResult<ReconciliationView>> Get(Guid atmId, Guid id, CancellationToken token)
        => Ok(await service.GetAsync(atmId, id, token));

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<ReconciliationView>(200)]
    public async Task<ActionResult<ReconciliationView>> Approve(Guid atmId, Guid id,
        [FromHeader(Name = "X-User"), Required, StringLength(100)] string user, CancellationToken token)
        => Ok(await service.ApproveAsync(atmId, id, user, token));
}
