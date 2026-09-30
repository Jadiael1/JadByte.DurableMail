# Implementation validation — 0.1.0

Validated locally on 2026-09-27 with .NET SDK 10.0.401 on Windows.

## Delivered

- Four .NET 10 packages: Core, EntityFrameworkCore, AspNetCore and MailKit.
- Isolated durable enqueue, transactional deduplication and supersession.
- Run-to-completion dispatch, per-instance single flight and configurable parallelism.
- Persistent attempts, optimistic claims, expiring leases and recovery of abandoned attempts.
- Exponential retry with jitter, one-shot scheduling and startup rehydration.
- Optional enqueue, middleware and startup triggers; manual dispatch and bounded pruning.
- SMTP adapter, validated options, sanitized diagnostics and structured runtime logs.
- Minimal API sample, public XML documentation, README and CI without publication.

## Verification

| Check | Result |
| --- | --- |
| `dotnet restore` | Passed |
| `dotnet format --verify-no-changes --no-restore` | Passed |
| `dotnet build -c Release --no-restore` | Passed; zero warnings and errors |
| Core tests | 34 passed |
| EF Core tests | 29 passed |
| ASP.NET Core tests | 7 passed |
| MailKit tests | 50 passed |
| `dotnet test -c Release --no-build --report-xunit-trx` | 120 passed; none skipped |
| `dotnet pack -c Release --no-build --output artifacts/packages` | Four packages generated |
| Package inspection | Expected IDs, version 0.1.0, net10.0 assemblies, XML documentation, README and dependency boundaries verified |
| Sample HTTP smoke test | Enqueue returned 202, simulated delivery succeeded, manual pruning removed delivery and attempt |

Relational tests use SQLite files and separate contexts, including controlled concurrent claims and slot-insertion races. Integration tests cover independent producer transactions, two coordinators sharing a database, persisted retry rehydration and lease recovery after restart. SMTP tests use simulated sessions; no external emails were sent. CI is configured but was not executed on a remote GitHub runner during this validation.

## Implementation refinements

- The EF adapter references `Microsoft.EntityFrameworkCore.Relational` for relational mappings and transactions; no production database provider is included.
- Timestamps use UTC ticks in 64-bit integer columns so ordering and comparisons work on SQLite. Offset presentation is normalized to UTC.
- The test runner uses Microsoft Testing Platform through `global.json`, matching xUnit v3 4.x and .NET 10. Individual test projects require `dotnet test --project ...`.
- Enqueue suppresses ambient transactions and creates its own service scope. A business transaction rollback does not roll back a committed email enqueue.
- Interrupted dispatch operations retain their signals and, when Scheduled is enabled, retry infrastructure failures with one-shot backoff. Successful idle drains leave no timer or polling loop.
- Error messages from transports are replaced by safe summaries before persistence. MailKit strips remote diagnostic content and credentials while preserving failure classification.
- Excessively distant retry and lease timestamps saturate at `DateTimeOffset.MaxValue` rather than overflow. EF mutations work with consumer contexts configured for no tracking or disabled automatic change detection.

## Publication

Packages are in `artifacts/packages`. No package was published, and no license or repository URL was chosen on the author's behalf.

NuGet's flat-container endpoint returned HTTP 404 for all four package IDs on the validation date. This indicates no published versions were found; it does not reserve an ID or guarantee publication rights. Recheck immediately before the first release, choose license and repository metadata, and review the packages before publishing.
