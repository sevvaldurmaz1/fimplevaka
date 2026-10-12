using System.ComponentModel.DataAnnotations;
using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

public sealed record CreateAtmRequest([Required, StringLength(100)] string Name);
public sealed record ConfigureAtmRequest([Required, MinLength(4), MaxLength(4)] CassetteConfiguration[] Cassettes);
public sealed record LoadCashRequest([Required, MinLength(1), MaxLength(60)] CashLoad[] Notes);
public sealed record ChangeStateRequest([Required] AtmState? State);

[ApiController]
[Route("api/atms")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
public sealed class AtmsController(AtmService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<AtmView>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AtmView>> Create(CreateAtmRequest request, CancellationToken cancellationToken)
    {
        var atm = await service.CreateAsync(request.Name, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = atm.Id }, atm);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<AtmView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AtmView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await service.GetAsync(id, cancellationToken));

    [HttpPut("{id:guid}/configuration")]
    [ProducesResponseType<AtmView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AtmView>> Configure(Guid id, ConfigureAtmRequest request, CancellationToken cancellationToken)
        => Ok(await service.ConfigureAsync(id, request.Cassettes, cancellationToken));

    [HttpPost("{id:guid}/loads")]
    [ProducesResponseType<AtmView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AtmView>> Load(Guid id, LoadCashRequest request, CancellationToken cancellationToken)
        => Ok(await service.LoadAsync(id, request.Notes, cancellationToken));

    [HttpPut("{id:guid}/state")]
    [ProducesResponseType<AtmView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AtmView>> ChangeState(Guid id, ChangeStateRequest request, CancellationToken cancellationToken)
        => Ok(await service.ChangeStateAsync(id, request.State!.Value, cancellationToken));

    [HttpPost("{id:guid}/unloads")]
    [ProducesResponseType<AtmView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AtmView>> Unload(Guid id, LoadCashRequest request, CancellationToken cancellationToken)
        => Ok(await service.UnloadAsync(id, request.Notes, cancellationToken));
}
