using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AtmCash.IntegrationTests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"atm_tests_{Guid.NewGuid():N}";
    private readonly string _adminConnection = Environment.GetEnvironmentVariable("ATM_TEST_DATABASE")
        ?? "Host=localhost;Port=54329;Database=postgres;Username=atm;Password=atm_local_only";

    public string ConnectionString { get; private set; } = string.Empty;
    public WebApplicationFactory<Program> Application { get; private set; } = null!;
    public ManualTimeProvider Clock { get; } = new();

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        // Database name is generated locally; no user-controlled SQL identifier is interpolated.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
        await create.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName };
        ConnectionString = builder.ConnectionString;
        Application = new ApiFactory(ConnectionString, Clock);
    }

    public async Task DisposeAsync()
    {
        if (Application is not null) await Application.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class ApiFactory(string connectionString, TimeProvider clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:CashDatabase", connectionString)
                .UseSetting("Security:CardHashKey", "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=")
                .UseSetting("Reservations:EnableExpirationWorker", "false")
                .ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                });
    }
}
