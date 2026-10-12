using AtmCash.Application.Operations;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Route("api")]
[ProducesResponseType<ProblemDetails>(404)]
public sealed class ReportsController(ReportingService service) : ControllerBase
{
    [HttpGet("reports/operations")]
    [ProducesResponseType<OperationsReport>(200)]
    public async Task<ActionResult<OperationsReport>> Network(CancellationToken token) => Ok(await service.ReportAsync(null, token));

    [HttpGet("atms/{atmId:guid}/report")]
    [ProducesResponseType<AtmReport>(200)]
    public async Task<ActionResult<AtmReport>> Atm(Guid atmId, CancellationToken token)
        => Ok((await service.ReportAsync(atmId, token)).Atms.Single());

    [HttpGet("atms/{atmId:guid}/consistency")]
    [ProducesResponseType<ConsistencyView>(200)]
    public async Task<ActionResult<ConsistencyView>> Consistency(Guid atmId, CancellationToken token)
        => Ok(await service.ConsistencyAsync(atmId, token));
}
