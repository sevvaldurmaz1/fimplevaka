# Fimple ATM Cash Management

This project is a REST API for managing ATM cash in TRY, USD, and EUR. It covers
withdrawals, deposits, banknote reservations, reconciliation of physical counts
with system records, and operational reports. It was developed for the Fimple
software engineering internship case study.

**Technologies:** ASP.NET Core (.NET 10), Entity Framework Core, PostgreSQL, and Docker Compose.

**Latest verification — 12 October 2026:** **128 tests passed**, including 82 unit
and 46 PostgreSQL integration tests. A **21-step end-to-end workflow** was verified
in Docker, and the build completed with zero errors and zero warnings.

The [implementation plan](docs/implementation-plan.md#gereksinim-ve-doğrulama-eşleştirmesi)
maps Part A and B1–B10 requirements to their verification scenarios. The delivery
scope is the backend. Supporting documents in `docs/` are in Turkish.

## Running the application and tests

Requirements: Docker Engine/Desktop and Compose; .NET 10 SDK for local development
and testing. `global.json` allows roll-forward across stable .NET 10 feature bands.

```sh
docker compose up --build -d
```

- Swagger: http://localhost:8080/swagger
- OpenAPI: http://localhost:8080/swagger/v1/swagger.json
- Database readiness: http://localhost:8080/health
- API liveness: http://localhost:8080/health/live

Compose starts the API once PostgreSQL is ready. EF Core migrations are applied
automatically at API startup; a migration failure stops startup. Data is stored
in the `cash-data` volume and survives `docker compose down`.
Ports are bound to localhost: API 8080 and PostgreSQL 54329.

`.env.example` contains a sample local password and HMAC key. Create an `.env`
file to use different values; this file is excluded from Git. `CARD_HASH_KEY`
must be a Base64-encoded key of at least 32 bytes. Keep the key stable so the
same cards remain identifiable after a restart. The sample key is intended
for local development only.

```sh
docker compose up -d db
dotnet restore
dotnet run --project src/AtmCash.Api
```

If the API container is running, use `docker compose stop api` first to avoid
a port conflict with the local API. Local Development settings use the sample
connection and key. To customize them, provide the `ConnectionStrings__CashDatabase`
and `Security__CardHashKey` environment variables.

```sh
dotnet test tests/AtmCash.UnitTests
docker compose up -d db
dotnet test tests/AtmCash.IntegrationTests
# Or run all tests while the database is running:
dotnet test AtmCash.sln
```

Integration tests create a unique database for each test class on a real
PostgreSQL instance, apply migrations, and delete only their own temporary
database when finished. Tests fail if PostgreSQL is unavailable; they are not
silently skipped. Set `ATM_TEST_DATABASE` to use a different connection with
permission to create databases. Update the test connection if you use a custom
Compose password. HTTP tests use `WebApplicationFactory`.

Test coverage and results are described in the [verification section](#verification).
Migration commands are available in the
[design document](docs/design-decisions.md#migration-yönetimi).

## Architecture and key design decisions

```text
src/
  AtmCash.Domain/          ATM, cassette, money, reconciliation, and low-cash rules
  AtmCash.Application/     Use cases, DTOs, algorithms, and persistence contracts
  AtmCash.Infrastructure/  PostgreSQL/EF, migrations, HMAC, and outbox dispatch
  AtmCash.Api/             HTTP, validation, ProblemDetails, hosted services, Swagger/health
tests/
  AtmCash.UnitTests/       Domain, algorithm, HMAC, and threshold tests
  AtmCash.IntegrationTests/ Real PostgreSQL, HTTP, concurrency, and rollback tests
postman/                  Full end-to-end workflow and smaller examples
docs/                     Requirements mapping and design decisions
```

Business rules live in the Domain layer, which has no HTTP or database dependencies,
so they can be tested directly with unit tests. Application coordinates use cases,
Infrastructure provides persistence, and API wires dependencies and manages the
HTTP contract. Use-case-specific persistence contracts keep transaction and lock
boundaries explicit.

- **Dynamic programming:** With limited stock, a greedy selection can miss a valid
  combination. The algorithm finds the minimum number of banknotes for reachable
  amounts and offers suitable lower and upper alternatives when the exact amount
  cannot be dispensed. All eight B3 acceptance scenarios were tested, including
  a case where greedy selection fails.
- **Database locks:** ATM and card locks prevent the same banknotes from being
  reserved for two withdrawals and stop concurrent requests at different ATMs
  from exceeding a card's daily limit. These locks also work across multiple API instances.
- **Persistent idempotency:** Withdrawal confirmation and the record identifying
  repeated requests are stored in the same transaction. Repeating a successful
  request returns the same response without deducting cash again.
- **Ledger and outbox:** Stock changes, immutable movement records, and any resulting
  low-cash event are written in the same transaction. A rollback also rolls back
  these records; notification dispatch can be retried in the background.
- **Real PostgreSQL tests:** Locking, transactions, and parallel requests are verified
  against the database used by the application.

The algorithm example, lock order, daily limits, reconciliation, and outbox delivery
semantics are explained in the [technical design decisions](docs/design-decisions.md).

## API and Postman

| Request | Purpose |
| --- | --- |
| POST /api/atms | Create an ATM |
| GET /api/atms/{id} | Read inventory and state |
| PUT /api/atms/{id}/configuration | Configure the four recycling cassettes |
| POST /api/atms/{id}/loads or /unloads | Manually add or remove cash |
| PUT /api/atms/{id}/state | Transition between service and maintenance |
| POST /api/atms/{id}/withdrawals/quotes | Create a quote and reserve banknotes |
| GET or DELETE /api/atms/{id}/withdrawals/quotes/{quoteId} | Read or cancel a quote |
| POST /api/atms/{id}/withdrawals/quotes/{quoteId}/confirm | Confirm an amount; Idempotency-Key is required |
| POST /api/atms/{id}/deposits | Allocate banknotes, handle overflow, and return unaccepted notes |
| POST /api/atms/{id}/reconciliations | Submit counts for all five cassettes; X-User is required |
| GET /api/atms/{id}/reconciliations/{reconciliationId} | Read the audit record, counts, and differences |
| POST /api/atms/{id}/reconciliations/{reconciliationId}/approve | Approve with a different X-User |
| GET /api/atms/{id}/consistency | Check ledger and inventory differences |
| GET /api/atms/{id}/report | Read the ATM report |
| GET /api/reports/operations | Read the network report |

The [end-to-end Postman collection](postman/atm-cash-flow.postman_collection.json)
covers the full delivery workflow: create → configure → load → enter service →
withdrawal quote → confirm → deposit → maintenance → reconcile → approval by a
different user → consistency check → report → return to service.

Its 21 steps also check confirmation replay, cancellation, overflow and returned
banknotes, rejection of approval by the submitting user, and rejection of a return
to service while approval is pending. Run the requests sequentially with the Runner.
`baseUrl` defaults to localhost:8080; `atmId`, `quoteId`, `reconciliationId`, and
an `idempotencyKey` unique to each run are assigned automatically. Confirm the
withdrawal within 60 seconds. The `atm-setup` and `atm-withdrawals` collections
provide smaller examples.

Deposit request:

```json
{
  "notes": [{ "currency": "TRY", "denomination": 100, "count": 6 }],
  "unrecognizedCount": 2
}
```

For reconciliation, `cassettes` contains five entries. Each entry has a `number`
and a `notes` array containing currency, denomination, and `count`. An empty
`notes` array represents an empty cassette. A mixed cassette includes all counted
banknote types. Complete examples are available in Swagger and the collection.

## Assumptions

- Cassette numbers are fixed: 1–4 are recycling and 5 is deposit-only; each holds 100 banknotes.
- Names do not have to be unique; ATM identifiers are GUIDs.
- The initial cash load requires at least one banknote. Reconciliation cannot
  bypass the initial load. The next state after LOADED is IN_SERVICE.
- Maintenance cannot start while reservations are active. A cassette's currency
  or denomination can change only after it has been emptied.
- Requests exceeding the remaining daily withdrawal limit are rejected.
  A zero-valued lower alternative is not offered.
- Card input is 13–19 ASCII digits without spaces; Luhn validation was not required.
- Banknote counts and amounts are integers; TRY conversion uses `decimal`.
  Initial exchange rates TRY=1, USD=35, and EUR=40 are sample values, not live market
  rates. Rates are stored in the ExchangeRates table; later rate changes do not
  affect reconciliation history.
- Duplicate banknote types in a deposit are combined. Groups use their first
  occurrence order when sharing deposit-cassette capacity. A request may contain
  up to 60 groups; large returned counts use `long` totals. Responses report
  cassette-level counts rather than banknote serial numbers.
- Unrecognized and returned banknotes do not enter inventory or the ledger.
  Requests containing only unrecognized banknotes are accepted; completely empty
  requests are rejected.
- Withdrawal confirmation provides idempotency-key handling. Repeated deposits,
  loads, unloads, or reconciliation submissions are new requests. Cancellation can be repeated.
- X-User values are trimmed and compared case-insensitively for approval;
  authentication was not required by the case study.
- The reconciliation threshold uses the sum of absolute differences converted
  to TRY, rather than the net difference. Differences above the threshold are
  applied after approval. An applied reconciliation cannot be approved again.

## Completion status and known limitations

Part A and B1–B10 are complete; scope and verification are documented in the
[requirements mapping](docs/implementation-plan.md#gereksinim-ve-doğrulama-eşleştirmesi).
Production features such as authentication and card ownership checks, physical
hardware and customer account integration, live exchange rates, key rotation,
and pagination for the network report are not implemented. They were not
required by the case study.

Outbox logging uses at-least-once delivery semantics: a crash between logging
and committing `SentAt` can cause the same event to be logged again. Audit and
ledger records are retained; retention and archival policies have not been added.
Deposit responses reference accepted movements, but there is no separate deposit
history endpoint.

## Verification

On 12 October 2026, all 82 unit and 46 real PostgreSQL integration tests passed,
with no skipped tests. Coverage includes the eight B3 acceptance scenarios,
concurrent quotes and deposits across two API instances, card limits across
multiple ATMs, idempotency, expiry and midnight transitions, reconciliation and
approval, unloading, reports and ledger consistency, and outbox rollback, retry,
and parallel dispatchers.

Migration and model consistency were checked. The 21-step collection workflow
passed against the API running in Docker, including setup, withdrawal confirmation
replay, deposits and returned banknotes, cancellation, maintenance, reconciliation
approval by a different user, ledger consistency, and operational reporting.
Expected HTTP 409 responses were also checked. Swagger and health returned
HTTP 200, and the build completed with zero warnings and zero errors.
A low-cash event was written to the outbox and processed by the background
service in Docker, with `SentAt` populated.

## AI usage

OpenAI Codex was used to support requirements analysis, code development, debugging, testing, and documentation throughout the project. The application was validated through build checks, unit tests, PostgreSQL integration tests, and end-to-end scenarios in a Docker environment. Additionally, critical behaviors such as concurrency, idempotency, time-sensitive operations, and transaction rollback were tested to ensure system reliability and data consistency.
