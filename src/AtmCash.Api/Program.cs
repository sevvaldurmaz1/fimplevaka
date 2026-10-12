using System.Text.Json.Serialization;
using AtmCash.Api.Background;
using AtmCash.Api.Errors;
using AtmCash.Api.Health;
using AtmCash.Application.Atms;
using AtmCash.Application.Withdrawals;
using AtmCash.Infrastructure.Persistence;
using AtmCash.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("CashDatabase")
    ?? throw new InvalidOperationException("ConnectionStrings:CashDatabase is required.");
builder.Services.AddDbContext<CashDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IAtmStore, PostgresAtmStore>();
builder.Services.AddScoped<AtmService>();
builder.Services.AddScoped<IWithdrawalStore, PostgresWithdrawalStore>();
builder.Services.AddScoped<WithdrawalService>();
builder.Services.AddSingleton<CassetteAllocator>();
var cardHashKey = builder.Configuration["Security:CardHashKey"]
    ?? throw new InvalidOperationException("Security:CardHashKey is required.");
builder.Services.AddSingleton<ICardFingerprint>(new HmacCardFingerprint(cardHashKey));
if (builder.Configuration.GetValue("Reservations:EnableExpirationWorker", true))
    builder.Services.AddHostedService<ReservationExpirationWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NoteSelector>();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = context =>
{
    var problem = new ValidationProblemDetails(context.ModelState)
    {
        Status = 400, Title = "Request validation failed", Instance = context.HttpContext.Request.Path
    };
    problem.Extensions["code"] = "validation_failed";
    problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    var result = new BadRequestObjectResult(problem);
    result.ContentTypes.Add("application/problem+json");
    return result;
});
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", "http_error");
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "ATM Cash Management", Version = "v1", Description = "Fimple software developer assignment backend."
}));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);

var app = builder.Build();
// Required by the assignment. A failed migration prevents the API from accepting traffic.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CashDbContext>();
    await db.Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.Run();

public partial class Program;
