# Changelog

## 0.1.0 — Unreleased

- Introduced the .NET 10 core, EF Core persistence, ASP.NET Core retry middleware and MailKit SMTP packages.
- Added durable enqueue, transactional deduplication and supersession, attempt history, optimistic claims and lease recovery.
- Added bounded run-to-completion dispatch, exponential backoff with jitter, one-shot scheduled retries and optional enqueue, middleware and startup triggers.
- Added manual retry dispatch and bounded terminal-delivery retention.
- Added relational SQLite tests, deterministic time-based tests and a runnable Minimal API sample.
- Established centralized package versions, nullable analysis, XML API documentation, warnings-as-errors builds and CI packaging.

Implementation details refining the plan: EF persistence references `Microsoft.EntityFrameworkCore.Relational` for relational table mapping and transactions, without selecting a database provider. Tests use the native Microsoft Testing Platform integration required by xUnit v3 4.x on .NET 10, selected in `global.json`; the Visual Studio adapter remains available for compatible IDEs.

Timestamps persist as UTC ticks for portable relational comparisons. EF automatic transactions are required, and Core suppresses ambient transactions to preserve independent enqueue commits. Transport exception messages are sanitized before persistence. Manual dispatch cancellation cancels the caller's wait without interrupting shared dispatch work.

No public release has been published. License, repository URL and package-name availability must be confirmed by the author before publication.
