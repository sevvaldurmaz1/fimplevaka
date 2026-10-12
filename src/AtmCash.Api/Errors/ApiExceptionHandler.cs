using AtmCash.Application.Atms;
using AtmCash.Application.Withdrawals;
using AtmCash.Application.Operations;
using AtmCash.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Errors;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = exception switch
        {
            AtmNotFoundException => (404, "atm_not_found", "ATM not found", exception.Message),
            QuoteNotFoundException => (404, "quote_not_found", "Quote not found", exception.Message),
            ReconciliationNotFoundException => (404, "reconciliation_not_found", "Reconciliation not found", exception.Message),
            DomainException domain when domain.Code == "exchange_rate_unavailable"
                => (503, domain.Code, "Exchange rate unavailable", domain.Message),
            DomainException domain when domain.Code == "quote_expired"
                => (410, domain.Code, "Quote expired", domain.Message),
            DomainException domain when domain.Code is "invalid_atm_state" or "invalid_state_transition" or "cassette_not_empty"
                or "idempotency_conflict" or "quote_cancelled" or "quote_already_confirmed"
                or "reconciliation_awaiting_approval" or "same_user_approval" or "reconciliation_already_applied" or "reconciliation_stale" or "active_reservations"
                => (409, domain.Code, "Operation conflicts with ATM state", domain.Message),
            DomainException domain => (422, domain.Code, "Business rule violation", domain.Message),
            _ => (500, "internal_error", "Unexpected server error", "An unexpected error occurred. Use the traceId when reporting it.")
        };
        if (status == 500)
            logger.LogError(exception, "Unhandled error. TraceId: {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = context.Request.Path };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
        return true;
    }
}
