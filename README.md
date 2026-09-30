# JadByte.DurableMail

Durable email delivery for .NET 10, with persisted attempts, retry, deduplication and concurrency control. Enqueue commits the message and returns without waiting for SMTP or another external transport.

The dispatcher runs to completion when signaled, drains eligible work and stops. There is no permanent `BackgroundService`, periodic database polling or recurring timer. Scheduled retry uses at most one local `ITimer`, armed with `Timeout.InfiniteTimeSpan` as its period.

Compiled packages are also distributed through GitHub Packages, as reported by the author. See [feed configuration, package consumption and compatibility notes](docs/sample1.md#nuget-packages-on-github-packages) before replacing local project references. This does not imply publication on nuget.org; license, repository metadata and package-name availability there remain author decisions.

Original implementation plan: [English](JadByte.DurableMail-implementation-plan.md) · [pt-BR](JadByte.DurableMail-implementation-plan.pt-BR.md).

## Packages

| Package | Responsibility |
| --- | --- |
| `JadByte.DurableMail` | Queue, dispatch, retry, scheduling, lifecycle and adapter contracts |
| `JadByte.DurableMail.EntityFrameworkCore` | Relational persistence, claims, leases, deduplication and retention |
| `JadByte.DurableMail.AspNetCore` | Optional HTTP retry trigger |
| `JadByte.DurableMail.MailKit` | SMTP transport and MailKit failure classification |

Core has no dependency on EF Core, ASP.NET Core or MailKit. There is no aggregate `All` package. The library does not provide generic scheduling, a message broker, attachments, templates, a dashboard, rate limiting or ASP.NET Core Identity integration.

## Build and run

The repository pins SDK `10.0.401` with patch roll-forward in `global.json`. Tests use its Microsoft Testing Platform runner selection, required by xUnit v3 4.x on .NET 10. To run one project, use `dotnet test --project tests/JadByte.DurableMail.Tests -c Release`; project paths are named arguments in this mode.

```sh
dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet pack -c Release --no-build --output artifacts/packages
```

### Containerized sample (sample1)

The [sample1 setup and validation guide](docs/sample1.md) ([pt-BR version](docs/sample1.pt-BR.md)) documents the generated ASP.NET Core Identity application, MariaDB persistence, Mailpit, queue dashboard, Prometheus/Grafana, VS Code actions and k6 load test. It exercises the library through local project references.

From the repository root, in Windows Command Prompt:

```bat
call scripts\dotnet\windows\create_projects.bat
cd samples\sample1
docker compose up -d --build
```

The first command creates the projects, extracts `scripts/dotnet/sample1.zip` and publishes the application for Linux x64. Starting Docker is a separate, manual step. The Compose file mounts that host publication into `/app`, so keep it current when changing code. Open the API at `http://localhost:8080`, the queue dashboard at `http://localhost:3005`, and Mailpit at `http://localhost:8025`.

Read the guide before using **GenerateMigration**, which deletes the migrations and database, or the k6 wrapper, which deletes Prometheus history. This is a local development environment with SMTP Chaos enabled, not a production deployment configuration.

## Register persistence and transport

Reference the packages you need and install a compatible relational EF Core provider in your application. The following example assumes the consumer selected SQLite:

```csharp
using JadByte.DurableMail;
using JadByte.DurableMail.EntityFrameworkCore;
using JadByte.DurableMail.MailKit;
using Microsoft.EntityFrameworkCore;

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Application")));

builder.Services
    .AddDurableMail(options =>
    {
        options.Dispatch.AutoDispatchOnEnqueue = true;
        options.Retries.MaxAttempts = 5;
        options.Retries.Scheduled.Enabled = true;
    })
    .UseEntityFrameworkCore<ApplicationDbContext>()
    .UseMailKit(options =>
        builder.Configuration.GetSection("Smtp").Bind(options));

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddDurableMail();
    }
}
```

Options are validated when the host starts. A custom `TimeProvider` can be registered before `AddDurableMail`; the default is `TimeProvider.System`.

### Migrations and provider compatibility

`ModelBuilder.AddDurableMail()` adds three internal entities for deliveries, attempts and active deduplication slots, using explicit snake_case mappings. The EF adapter references `Microsoft.EntityFrameworkCore.Relational` for table mapping and transactions, but does not ship any database provider or universal migrations.

Timestamps are stored as UTC ticks in signed 64-bit integer columns, allowing ordered queries on providers such as SQLite that cannot compare native `DateTimeOffset` values. Public APIs retain `DateTimeOffset`; persisted values normalize the original offset to UTC. Keep this representation when inspecting data or extending the model.

Leave EF's automatic transactions enabled. The adapter rejects `AutoTransactionBehavior.Never`, an already active context transaction, or unrelated pending business changes, because each operation must commit all of its state atomically in an independent context.

Generate and deploy migrations from your application's project using its selected provider and the matching EF tools/Design package:

```sh
dotnet ef migrations add AddDurableMail
dotnet ef database update
```

Create the schema before starting the host, because schedule rehydration runs at startup. The generated [sample1](docs/sample1.md#migrations) applies existing migrations with `MigrateAsync()` before `app.RunAsync()`; it does not generate migrations at runtime.

The adapter uses the EF Core provider configured by the consumer. Concrete database compatibility depends on that provider's relational transaction, concurrency and query capabilities. SQLite is exercised by the repository's relational tests, including separate contexts and file-backed concurrency. Other databases require real integration tests before being described as officially tested. EF Core InMemory cannot validate these guarantees.

## Enqueue and deduplicate

```csharp
EmailEnqueueResult result = await queue.EnqueueAsync(
    new EmailEnqueueRequest(
        new EmailMessage("reader@example.com", "Welcome", "<p>Hello!</p>"),
        Source: "account:registration",
        DeduplicationKey: "welcome:account-123"),
    cancellationToken);
```

The enqueue operation has its own persistence scope and transaction. It never saves unrelated tracked changes in the request's `DbContext`. Core suppresses an ambient `TransactionScope` for its persistence operations, so the durable enqueue can commit even if the surrounding business transaction later rolls back. Consequently, v0.1 does **not** provide atomicity between the consumer's business transaction and the email enqueue. An application needing that guarantee must provide a separate outbox integration.

Omit `DeduplicationKey` to permit independent deliveries. With a key, a unique persistent slot represents one active logical delivery. The default policy keeps the existing delivery whether its `Source` matches or differs. The result contains the retained or new `DeliveryId` and `Created`, `Existing` or `SupersededAndCreated`.

```csharp
var policy = new EmailEnqueuePolicy
{
    SameSource = ActiveDeliveryConflictBehavior.KeepExisting,
    DifferentSource = ActiveDeliveryConflictBehavior.SupersedeExisting,
};
```

Supersession transactionally replaces slot ownership and prevents future retries of the old delivery. It cannot retract an email already accepted by an external transport or guarantee that an in-flight send is canceled. The previous worker cannot overwrite the replacement's state or release its slot. A completed delivery releases its slot, so this is active-delivery deduplication rather than permanent idempotency.

## Retry and triggers

| Option | Default |
| --- | --- |
| `Dispatch.AutoDispatchOnEnqueue` | `true` |
| `Dispatch.BatchSize` | `25` |
| `Dispatch.MaxDegreeOfParallelism` | `1` |
| `Dispatch.LeaseDuration` | 2 minutes |
| `Retries.MaxAttempts` | `5`, including the first attempt |
| `Retries.InitialDelay` | 2 minutes |
| `Retries.BackoffFactor` | `2` |
| `Retries.MaxDelay` | 30 minutes |
| `Retries.JitterRatio` | `0.15` |
| `Retries.Scheduled.Enabled` | `true` |
| `Retries.Enqueue.Enabled` | `false` |
| `Retries.Middleware.Enabled` | `false` |
| `Retries.Middleware.Interval` | 1 minute |
| `Retries.Startup.Enabled` | `false` |
| Manual dispatch | Always available |

Without jitter, the first three retry delays are 2, 4 and 8 minutes. The exponential base delay is capped at `MaxDelay` before proportional jitter is applied, so a randomized delay can exceed that base cap. Transient failures return to Pending with a persisted next-attempt time until the total attempt limit is reached. Permanent failures and exhausted attempts become Failed. Each claimed attempt remains in the history.

Scheduled retry uses one one-shot timer for the next persisted event. Startup rehydrates that schedule, recovers expired leases and signals overdue work when Scheduled is enabled. This recovery occurs independently of the optional Startup trigger, which requests an additional one-time drain.

`AutoDispatchOnEnqueue` signals the new delivery itself. The separate Enqueue retry trigger also scans other due work. With every automatic trigger disabled, existing work needs manual dispatch or another configured signal; there is no hidden polling fallback.

All triggers share the same local single-flight coordinator. Concurrent signals are coalesced, and a signal arriving during dispatch is processed before the runner exits. Each parallel delivery gets its own scope and transport.

When an already signaled dispatch fails because persistence is temporarily unavailable, the coordinator retains its pending signals. With Scheduled enabled, it retries that interrupted operation through a one-shot timer using exponential backoff and jitter. Infrastructure failures do not consume transport attempts. A successful drain resets this recovery backoff and removes unnecessary timers; an idle queue is never polled. Manual callers receive the dispatch failure, and can retry explicitly when Scheduled is disabled.

### Optional ASP.NET Core middleware

```csharp
using JadByte.DurableMail.AspNetCore;

// In AddDurableMail's configuration:
options.Retries.Middleware.Enabled = true;
options.Retries.Middleware.Interval = TimeSpan.FromMinutes(1);

// In the HTTP pipeline, before endpoint execution:
app.UseDurableMailRetryTrigger();
```

Call the middleware extension explicitly. A disabled trigger only calls the next middleware. Place it after exception handling and before endpoint execution; it has no authentication dependency. It signals dispatch without awaiting database or transport work. The gate uses monotonic `TimeProvider` timestamps and atomic operations to throttle requests per instance. It is not a distributed lock or an HTTP rate limiter.

### Manual dispatch and maintenance

```csharp
EmailDispatchResult dispatch = await dispatcher.DispatchDueAsync(cancellationToken);

EmailMaintenanceResult pruned = await maintenance.PruneAsync(
    new EmailPruneRequest(
        SucceededBefore: timeProvider.GetUtcNow().AddDays(-30),
        FailedBefore: timeProvider.GetUtcNow().AddDays(-90),
        SupersededBefore: timeProvider.GetUtcNow().AddDays(-30),
        BatchSize: 100),
    cancellationToken);
```

Resolve `IEmailRetryDispatcher` and `IEmailDeliveryMaintenance` from DI. Manual dispatch suits a CLI, protected administrative endpoint or external scheduler. Pruning removes only terminal deliveries older than their exclusive cutoff and their attempt history, up to the requested batch limit. Null cutoffs retain that state. Pending and Processing deliveries are never removed by ordinary retention. Invoke additional batches from your own maintenance workflow when needed; DurableMail creates no periodic cleaner.

Canceling `DispatchDueAsync` cancels that caller's wait. It does not cancel the shared dispatch run, which may also be serving automatic triggers or another manual caller. Host shutdown controls the shared run's cancellation.

## Concurrency, leases and delivery guarantees

The database is the source of truth. A claim atomically changes the delivery's application-managed `Guid` concurrency token, assigns a lease, increments the attempt count and creates the attempt record. A losing claimant does not send. Completion requires the matching lease and version; stale workers cannot overwrite newer claims.

An expired Processing lease is recovered as an Abandoned attempt, then retried with backoff when attempts remain or marked Failed when exhausted. Shutdown cancellation does not become a permanent transport failure. A canceled in-flight attempt can be left for lease recovery if there is no safe opportunity to persist its final state.

Delivery is **at least once**. If a transport accepts an email and the process stops before success is committed, recovery can send it again. SMTP has no transactional relationship with the database, and DurableMail does not promise exactly-once delivery.

Keep the transport's total operation timeout below `Dispatch.LeaseDuration`, allowing margin for persistence. v0.1 does not renew leases for long-running transports. MailKit defaults to a 30-second timeout and a two-minute lease; increase the lease for slower custom transports.

Multiple instances share database claims and slots but have independent signals and timers. Signals are not broadcast between processes. Scheduled startup rehydration restores known persistent work after a restart; an instance does not continuously discover new work created elsewhere. Enable an appropriate opportunistic trigger or call manual dispatch from an external scheduler when you need discovery across otherwise idle instances.

## MailKit and custom transports

MailKit creates one SMTP client per send, supports HTML and plain text, optional sender name and coherent optional username/password credentials. `StartTls` is the default; explicit alternatives are `Auto`, `StartTlsWhenAvailable`, `SslOnConnect` and `None`. SMTP 4xx and connection/timeout errors are transient; SMTP 5xx, invalid messages and authentication failures are permanent. Cancellation remains cancellation.

To supply another provider, register `IEmailTransport` and optionally `IEmailFailureClassifier`:

```csharp
builder.Services.AddDurableMail()
    .UseEntityFrameworkCore<ApplicationDbContext>();
builder.Services.AddScoped<IEmailTransport, ApiEmailTransport>();

internal sealed class ApiEmailTransport(EmailApiClient client) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        => client.SendAsync(message.Recipient, message.Subject, message.Body, cancellationToken);
}
```

`EmailApiClient` represents your application/provider client. Core's default classifier treats unknown errors as transient; `MaxAttempts` bounds retries. A specialized classifier can distinguish permanent provider errors. Custom adapters must observe cancellation and avoid including credentials or payloads in diagnostics. Public persistence contracts live in `JadByte.DurableMail.Storage` for alternative durable stores.

## Sensitive data and integration boundaries

Delivery tables persist recipients, subjects and bodies. They can contain personal data, confirmation links, password-reset codes and tokens: treat the database, backups and access permissions as sensitive. Use the consumer's established storage protection and retention policies. Payload encryption is outside v0.1; no custom encryption scheme is supplied.

Structured runtime logs identify operations without logging bodies, tokens or SMTP credentials. Arbitrary transport exception messages are replaced with a generic failure classification before persistence, because a provider error can echo sensitive input. Persisted diagnostics retain a bounded exception type and stack trace without source-file information; the EF adapter additionally limits error type, message and stack to 512, 4,096 and 16,384 characters. Custom store callers are responsible for sanitizing their own `EmailFailureDetails`. Do not enable EF sensitive-data logging in production, and avoid putting secret values into source metadata or deduplication keys.

ASP.NET Core Identity integration belongs in a consumer adapter around `IEmailQueue`. Supply source metadata from the business operation explicitly; DurableMail never infers registration or resend behavior from a URL. Authentication, endpoint rate limiting and business rules remain application responsibilities.

## Validation and release

The solution enforces nullable annotations, recommended analyzers, warnings as errors and English XML documentation for public production APIs. Tests cover Core using a fake store and `FakeTimeProvider`, relational behavior using SQLite, middleware throttling, and MailKit validation/classification without requiring a real SMTP server.

CI restores, verifies formatting, builds Release, tests and creates all four packages. GitHub actions are pinned to verified commits. The workflow uploads artifacts but does not publish packages to a registry; the author's GitHub Packages distribution is separate. Before publishing a new release, inspect the packages, confirm license/repository metadata and package-name availability, and review the provider compatibility evidence.
