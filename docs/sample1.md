# Sample1: validation environment with Docker

This guide describes how to generate and use the sample application to validate the local JadByte.DurableMail code: registration with ASP.NET Core Identity, persistent enqueueing, SMTP delivery, retries, queue queries, and observation during load tests.

The entry point is [`create_projects.bat`](../scripts/dotnet/windows/create_projects.bat). It creates the entire project in `samples/sample1`, applies the template from [`sample1.zip`](../scripts/dotnet/sample1.zip), and publishes the WebApi for Linux x64. **Creating and starting the Docker environment is a separate manual step**, using `docker compose up -d --build`.

The guide lives in `docs` so it can be read before generating the sample and remains available when the sample is recreated. The configuration paths mentioned below are relative to `samples/sample1` unless stated otherwise. The ZIP is the source of the template; changes made in the generated folder do not automatically update the ZIP.

Navigation: [first run](#preparation-and-first-run) · [services](#services-and-addresses) · [scripts and ZIP](#what-the-scripts-create) · [GitHub packages](#nuget-packages-on-github-packages) · [Docker and updates](#publishing-dockerfile-and-code-updates) · [migrations](#migrations) · [VS Code](#vs-code) · [queue and retries](#queue-dashboard-and-retries) · [metrics](#prometheus-and-grafana) · [k6](#load-testing-with-k6) · [troubleshooting](#common-situations).

## Preparation and first run

### Prerequisites

| Tool or condition | Purpose |
| --- | --- |
| Windows and `cmd.exe` | Run the provided `.bat` bootstrap and the VS Code `RUN` shortcuts. |
| .NET SDK as specified in [`global.json`](../global.json) | SDK `10.0.401`, with `latestPatch` and no preview versions, to create and publish the projects on the host. |
| `tar` available on PATH | Extract the ZIP template during bootstrap. |
| Docker with Linux containers and Compose | Build and run the seven services. `dockerfile_inline` requires Compose 2.17.0 or later, according to the [Docker reference](https://docs.docker.com/reference/compose-file/build/#dockerfile_inline). |
| Linux x64-compatible environment | The publishing script explicitly uses `-r linux-x64`; the scripts do not select the architecture automatically. |
| Internet access | Restore NuGet/npm dependencies, download images, and download the Grafana dashboard during the build. The browser also uses CDNs for the queue dashboard interface. |
| Available ports | `8080`, `3005`, `3306`, `8081`, `1025`, `8025`, `9090`, and `3000`. |

Node.js/npm on the host are not required to run the dashboard in a container. `k6` is only required for the load test. `dotnet-ef` and the `mysql` client are only required for the optional migration management workflow described later; they are not required for the initial application of the migrations already provided.

### 1. Generate the sample

Open **Windows Command Prompt at the repository root** and run:

```bat
call scripts\dotnet\windows\create_projects.bat
```

This is the only script needed for the initial setup. There is no need to call each helper manually. It resolves the root from its own location, creates the projects and configures their references, extracts the template, and invokes publishing.

When it finishes, confirm that publishing completed and that the following exists:

```text
samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/
  bin/Release/net10.0/linux-x64/publish/
    JadByte.DurableMail.Samples.Sample1.WebApi.dll
    JadByte.DurableMail.Samples.Sample1.Infrastructure.dll
```

The current bootstrap does not check every internal command's exit code; check the log and the published artifact as well as the script's return code. If `samples/sample1/src` already exists, it reports `Sample1 already exist.` and exits: **it does not update or repair an incomplete generation**. Preserve local changes and identify the failed step before trying to recreate the environment.

### 2. Check the local configuration

The ZIP already includes `.env`, `.env.example`, `secrets/db_password`, and its example file. Review the extracted files before the first startup; there is no need to overwrite them with the examples when they already exist.

| Configuration | Source and use |
| --- | --- |
| `MARIADB_HOST` | `.env`; the template value is `mariadb`, the service's DNS name within Compose. |
| `MARIADB_NAME` | `.env`; the application database, initially `jad_byte_durable_mail_sample1`. |
| `MARIADB_USER` | `.env`; the application user, initially `jadByteSample1`. |
| Database password | `secrets/db_password`; the `db_password` secret, mounted at `/run/secrets/db_password`. It is not stored in `MARIADB_PASSWORD` in `.env`. |
| `SMTP_HOST` / `SMTP_PORT` | `.env`; `mailpit` and `1025` for communication within the Docker network. |
| `SMTP_FROMNAME` / `SMTP_FROMEMAIL` | `.env`; the sample's sender. |
| `SMTP_USERNAME` / `SMTP_PASSWORD` | Empty in the template; the local SMTP server does not require authentication. |
| `SMTP_SECURITYMODE` | `4`, corresponding to `None` in the library's MailKit transport, for this local SMTP server without TLS. |
| `SMTP_USESTARTTLS` | The template also passes this variable to the `Smtp` section; the library's transport security is selected through `SecurityMode`. |
| `LUCKYPENNY_LICENSE_KEY` | An empty template field passed as `LicenseKeys__LuckyPenny`; it is not a MariaDB or Mailpit credential. |

In the WebApi, the Dockerfile entrypoint reads the secret and assembles `ConnectionStrings__DefaultConnection` with `Server`, `Port=3306`, `Database`, `User Id`, `Password`, and `TreatTinyAsBoolean=false`. It also writes exports to `/root/.bashrc` inside the container. In MariaDB, `MARIADB_PASSWORD_FILE` points to the same secret. In the dashboard, the copy of `server.js` in the image reads this file directly.

Inside the containers, use `mariadb:3306` and `mailpit:1025`. Tools running on Windows use the published ports, such as `localhost:3306` and `localhost:1025`. Compose uses `.env`; it does not automatically configure a WebApi started directly by the .NET debugger.

`nuget.config.example` is only an example feed configuration with placeholders. The bootstrap does not activate it; a GitHub token is not required to reference the local libraries. Optional consumption of published packages is described in [NuGet packages on GitHub Packages](#nuget-packages-on-github-packages).

### 3. Start the containers

After generation, enter the sample folder and run:

```bat
cd samples\sample1
docker compose config --quiet
docker compose up -d --build
docker compose ps
docker compose logs --tail 100 webapi
```

`create_projects.bat` does not perform this step. The WebApi, dashboard, and phpMyAdmin wait for MariaDB's healthcheck. The application applies pending migrations before it starts serving HTTP; if this fails, the error appears in the WebApi logs. The database healthcheck does not, by itself, indicate that the application's migrations have completed: wait for the API to be ready before starting the load test.

## Services and addresses

| Compose service | Host access | Responsibility |
| --- | --- | --- |
| `webapi` | `http://localhost:8080/` | The sample API and Scalar interface in Development; `/metrics` exposes metrics. |
| `durable-mail-dashboard` | `http://localhost:3005/` | Queries the queue in MariaDB and displays statuses, attempts, and errors. |
| `mariadb` | `localhost:3306` | The application database, Identity tables, and DurableMail persistence; it is not an HTTP page. |
| `phpmyadmin` | `http://localhost:8081/` | Web-based MariaDB inspection; the template connects as `root` without a password. |
| `mailpit` | `http://localhost:8025/`; SMTP at `localhost:1025` | Receives test emails and allows inspection; injects failures with Chaos. |
| `prometheus` | `http://localhost:9090/` | Collects WebApi metrics and stores time series. |
| `grafana` | `http://localhost:3000/` | Visualizes Prometheus time series; on a fresh installation, the login is `admin` / `admin`. |

Container names are fixed: `jadByteSample1` for the API and `jadByteSample1-<serviço>` for the others, including `jadByteSample1-durable-mail-dashboard`. Concurrent instances of this same Compose setup may conflict on names, ports, and the explicitly named MariaDB volume.

The setup is configured for local development: the API runs in Development, some services have unauthenticated interfaces, the database has a passwordless root user, and demonstration credentials are used. The published ports are not restricted to `127.0.0.1` in the YAML. Use test data; the Mailpit, phpMyAdmin, and dashboard interfaces may expose recipients, content, and confirmation/reset links.

## What the scripts create

### Projects and implementation

The bootstrap generates three class libraries and one WebApi, removes the templates' `Class1.cs` files, and adds all four projects to the root solution, `JadByte.DurableMail.slnx`.

| Project | References and role in the sample |
| --- | --- |
| `JadByte.DurableMail.Samples.Sample1.Domain` | Base layer created by the template. |
| `JadByte.DurableMail.Samples.Sample1.Application` | References Domain; receives the application packages. |
| `JadByte.DurableMail.Samples.Sample1.Infrastructure` | References Domain, Application, and the local `JadByte.DurableMail.EntityFrameworkCore` and `JadByte.DurableMail.MailKit` projects; implements persistence and Identity email delivery. |
| `JadByte.DurableMail.Samples.Sample1.WebApi` | References Application and Infrastructure; configures endpoints, migrations, and metrics. |

Local references reach Core through the adapters' dependencies. This workflow lets you validate library code changes without publishing its packages to NuGet. Having the library's ASP.NET Core project in the Docker context does not mean the optional retry middleware is enabled: the sample uses the scheduled trigger.

### Script catalog

All paths in this table are relative to the repository root.

| File | Responsibility |
| --- | --- |
| [`scripts/dotnet/windows/create_projects.bat`](../scripts/dotnet/windows/create_projects.bat) | Orchestrates creation, configuration, ZIP extraction, and publishing. Calls that add projects to the solution and configure references appear twice in the current workflow. It does not start Docker; the migration reset call is commented out. |
| [`scripts/dotnet/windows/inc/remove_version.bat`](../scripts/dotnet/windows/inc/remove_version.bat) | Removes the explicit `Microsoft.AspNetCore.OpenApi` version from the generated `.csproj` when it finds the literal `10.0.12`, so the centralized version is used. It does not generically remove all versions. |
| [`scripts/dotnet/windows/inc/create_props.bat`](../scripts/dotnet/windows/inc/create_props.bat) | Creates/overwrites the sample's `Directory.Packages.props` with Central Package Management and package versions. |
| [`scripts/dotnet/windows/adding_projects_to_sln.bat`](../scripts/dotnet/windows/adding_projects_to_sln.bat) | Adds the four projects to the solution, checking whether their paths are already present. |
| [`scripts/dotnet/windows/referencing_projects.bat`](../scripts/dotnet/windows/referencing_projects.bat) | Configures `ProjectReference` entries and calls the framework reference helper. |
| [`scripts/dotnet/windows/inc/add_framework_reference.bat`](../scripts/dotnet/windows/inc/add_framework_reference.bat) | Adds a `FrameworkReference` to `Microsoft.AspNetCore.App` in Infrastructure; checks for an existing reference and rejects the legacy package with that name. |
| [`scripts/dotnet/windows/add_package_to_project.bat`](../scripts/dotnet/windows/add_package_to_project.bat) | Adds application, Identity/Pomelo/MailKit, and OpenAPI/OpenTelemetry packages to their respective layers. |
| [`scripts/dotnet/windows/buildForDocker.bat`](../scripts/dotnet/windows/buildForDocker.bat) | Runs the lines beginning with `dotnet publish ` from `linux/publish.sh` on Windows. Publishes on the host; does not invoke Docker, Bash, or migrations. |
| [`scripts/dotnet/linux/publish.sh`](../scripts/dotnet/linux/publish.sh) | Publishes the WebApi in `Release` for `linux-x64`. Must be run from the root; does not set up the project and is not equivalent to the Windows bootstrap. |
| [`scripts/dotnet/windows/generate_migration.bat`](../scripts/dotnet/windows/generate_migration.bat) | Deletes migrations and the database, creates a new migration, and applies it. Restricted to deliberate resets of disposable data. |
| [`scripts/k6/loadtest.js`](../scripts/k6/loadtest.js) | Defines the registration HTTP load. |
| [`scripts/k6/run.bat`](../scripts/k6/run.bat) | Recreates Prometheus, deleting its history, and runs k6. |

The main package groups are AutoMapper/FluentValidation/MediatR in Application; Pomelo, Identity EF Core, MailKit, and snake_case conventions in Infrastructure; and Scalar, EF Design, and OpenTelemetry in WebApi. The version source is `Directory.Packages.props`, generated by the helper and also provided in the ZIP. The sample targets `net10.0`, but uses EF Core/Identity/Design `9.0.20` and Pomelo `9.0.0`; the target framework alone does not determine the EF version.

### ZIP contents

The ZIP has 47 entries, including directories. It contains Docker/environment configuration, VS Code files, the dashboard, development secrets, the Infrastructure/WebApi implementation, and the initial migration with its designer and snapshot. Its `README.md` is empty in the current template.

It does not contain the `.csproj` files or the Domain/Application template implementations. Therefore, **extracting the ZIP alone does not replace `create_projects.bat`**. The script generates the projects first, then extracts the ZIP, overlaying the supplied implementation/configuration files.

The internal `.gitignore` ignores, among other things, `.env`, local secrets, `appsettings.*json`, and `Migrations` folders. Having the migration in the ZIP does not mean future changes in the extracted folder will automatically be versioned. Review this when you decide to preserve the sample's evolution; this guide does not change those rules or the ZIP.

## NuGet packages on GitHub Packages

In addition to the source code, the author reports having published compiled packages on GitHub Packages and provided `0.1.0` as a consumption example. This is an alternative to `ProjectReference`: it validates the distributed artifact, not changes in local code that have not yet been packaged. The bootstrap continues to use local projects; it does not switch to packages automatically.

### Source and authentication

The ZIP's `nuget.config.example` keeps nuget.org for other dependencies and adds the `MyGitHub` source:

```xml
<packageSources>
  <clear />
  <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  <add key="MyGitHub" value="https://nuget.pkg.github.com/Jadiael1/index.json" />
</packageSources>
```

The address is the NuGet feed for the `Jadiael1` account, **not `https://github.com`**. To use the configuration, copy the example to `samples/sample1/nuget.config` and configure consumer authentication; the `.example` suffix prevents automatic use. The example also defines `globalPackagesFolder`, a cache folder relative to the configuration. Keeping both feeds available does not establish a fallback order between them.

For local registry access, use a **classic** personal access token; public packages in this registry also require authentication. The authentication username is the token owner's, not necessarily `Jadiael1`, which identifies the account that owns the feed. See the [GitHub NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry). To consume packages, the token needs `read:packages` and the account needs access; publishing requires `write:packages` and the corresponding permission. See [GitHub Packages permissions](https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages).

The example's `ClearTextPassword` contains only the mask `ghp_***...`, not a usable token. The inspected `.gitignore` files **do not ignore `nuget.config`**: do not store or version a real token in that file. One option is to replace the credential values with environment variable references:

```xml
<packageSourceCredentials>
  <MyGitHub>
    <add key="Username" value="%GITHUB_PACKAGES_USERNAME%" />
    <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
  </MyGitHub>
</packageSourceCredentials>
```

Set these variables in the restore process's environment using your local secrets mechanism; do not put their values in versioned scripts. NuGet interprets the `%VARIAVEL%` syntax in this XML. See [credentials for authenticated feeds](https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds). This does not automatically configure the Docker build.

### References and version compatibility

**Check the dependencies of the package you intend to consume first.** In the local inspection, `artifacts/packages/JadByte.DurableMail.EntityFrameworkCore.0.1.0.nupkg` declares EF Core/Relational `10.0.12`, but the sample uses Pomelo `9.0.0` and Identity/Design `9.0.20`. Pomelo's `.nuspec` in the local cache accepts Relational from `9.0.0` to `9.0.999`: this local `0.1.0` artifact is incompatible with the template's provider. The library's current source already uses EF Core `9.0.20`; an old `.nupkg` does not track source code changes.

The remote package's contents were not compared with the local artifact. Therefore, the `0.1.0` examples below demonstrate configuration; **they do not establish that this published version is compatible with the sample**. Before switching, select a published version whose dependencies align with the provider and the application's EF/Identity/Design packages. Do not change only the EF version while ignoring the range supported by Pomelo.

In a consumer project **without Central Package Management**, the Infrastructure references can declare the version directly:

```xml
<ItemGroup>
  <PackageReference Include="JadByte.DurableMail.EntityFrameworkCore" Version="0.1.0" />
  <PackageReference Include="JadByte.DurableMail.MailKit" Version="0.1.0" />
</ItemGroup>
```

Central management is enabled in the generated sample. In that case, add the versions to the existing `ItemGroup` in `samples/sample1/Directory.Packages.props`:

```xml
<PackageVersion Include="JadByte.DurableMail.EntityFrameworkCore" Version="0.1.0" />
<PackageVersion Include="JadByte.DurableMail.MailKit" Version="0.1.0" />
```

And use the following in Infrastructure's `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="JadByte.DurableMail.EntityFrameworkCore" />
  <PackageReference Include="JadByte.DurableMail.MailKit" />
</ItemGroup>
```

Remove **only** the two `ProjectReference` entries for the corresponding local adapters; keep the Domain/Application references, the `FrameworkReference`, and the other packages. Core is a transitive dependency of the adapters and must also be available in the feed. The sample does not require the optional `JadByte.DurableMail.AspNetCore` package. The separation between `PackageVersion` and `PackageReference` follows [Central Package Management](https://learn.microsoft.com/pt-br/nuget/consume-packages/central-package-management); duplicating `Version` in the `.csproj` in this mode causes `NU1008`.

After aligning versions and configuring credentials, an explicit restore **from the repository root** is:

```bat
dotnet restore samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj --configfile samples/sample1/nuget.config
```

Running `referencing_projects.bat` again reintroduces local references. `create_props.bat` or another ZIP extraction may also overwrite customizations. This conversion is an optional consumer configuration, not a required step for testing this repository's code.

### Docker and package publishing

The provided Dockerfile **does not copy `nuget.config` before restore or supply GitHub credentials to the build**. A working authenticated restore on Windows does not authenticate restore inside the image. Compose's `environment` variables and the MariaDB secret are runtime configuration and do not provide this access. To use packages in this build, you will need to configure the source and make the credential temporarily available during restore without embedding it in the image. This adaptation is not implemented in the current template. The `/app` bind mount still requires an up-to-date host publication.

For the **maintainer** to publish an already generated and reviewed `.nupkg`, the command format in Command Prompt is:

```bat
dotnet nuget push "CAMINHO_DO_PACOTE.nupkg" --source "https://nuget.pkg.github.com/Jadiael1/index.json" --api-key "%GITHUB_PACKAGES_TOKEN%"
```

Replace the path with the artifact and use a token authorized to publish, already configured in the environment; do not share logs that expose it. This command changes the remote registry, is not required for consumers, and is not run by the bootstrap. The `pack` commands in the [README](../README.md#build-and-run) generate packages in `artifacts/packages`; generating a package and uploading it to GitHub are separate steps. The feed and push formats are documented in the [GitHub NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

## Publishing, Dockerfile, and code updates

The WebApi build uses `context: ../..`, the repository root, and `dockerfile: samples/sample1/Dockerfile`. This lets the Dockerfile copy both library and sample projects. `Dockerfile.dockerignore` excludes `bin`, `obj`, `.git`, `.vs`, and `.vscode` from this context.

The Dockerfile has two stages: .NET 10 SDK for restore/publish and ASP.NET Runtime 10 to run the published DLL. Migrations are compiled into the Infrastructure assembly; the runtime does not need the `.cs` files or `dotnet-ef` to apply them.

The sample's empty `Directory.Build.props` is also copied before restore. As the closest file to the projects, it sets the boundary for the sample's configuration and does not automatically import root rules such as `TreatWarningsAsErrors=true` and `AnalysisLevel=latest-recommended`. Libraries in `src/` continue to use the root rules. This follows from [MSBuild's search for directory configuration](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory).

There is a key detail in the current Compose setup:

```yaml
volumes:
  - type: bind
    source: ./src/JadByte.DurableMail.Samples.Sample1.WebApi/bin/Release/net10.0/linux-x64/publish/
    target: /app
```

This mount makes the container use the binaries published on the host. The image content already present at `/app` is obscured, according to [bind mount semantics](https://docs.docker.com/engine/storage/bind-mounts/#bind-mounting-over-existing-data). This is why publishing at the end of `create_projects.bat` is necessary even though the Dockerfile also runs `dotnet publish`.

To update WebApi or library code in an existing environment, run this in `samples/sample1`:

```bat
call ..\..\scripts\dotnet\windows\buildForDocker.bat
```

After confirming that publishing succeeded:

```bat
docker compose restart webapi
```

The VS Code `RUN - RebuildForDocker` shortcut publishes and then runs `docker compose restart` without specifying a service, restarting all services. Running `docker compose up -d --build` alone rebuilds images but does not update the host's publish folder. Changes to Compose or environment variables require recreation through `up`; `restart` only restarts existing containers.

The dashboard has a different workflow: `server.js` and `public/` are copied into its image, with no code bind mount. After changing these files, rebuild the service with `docker compose up -d --build durable-mail-dashboard`.

For an already generated sample, publishing can be done on Linux with `bash scripts/dotnet/linux/publish.sh` from the repository root. This script does not provide a Linux bootstrap equivalent to the `.bat` files.

## Application workflow

```text
POST /v1/signUp
  -> ASP.NET Core Identity creates the user
  -> IEmailSender<ApplicationUser> requests confirmation
  -> IEmailQueue persists the delivery in MariaDB
  -> dispatcher sends through MailKit to mailpit:1025
  -> success or failure/attempt/retry are persisted
```

The Identity endpoint group is at `/v1`. The template renames registration to `/v1/signUp` and login to `/v1/signIn`; the other Identity endpoints remain in the group. Registration requires a unique email address, and sign-in requires confirmation.

The `EmailSender` adapter turns confirmation and password recovery into HTML messages with an explicit `Source`: `identity:confirmation`, `identity:password-reset`, or `identity:password-reset-code`. It calls `IEmailQueue.EnqueueAsync`; it does not provide a `DeduplicationKey` in this workflow.

For a manual check, open Scalar at `http://localhost:8080/` and send a `POST /v1/signUp` with an email address that has not yet been registered:

```json
{
  "email": "sample1-primeiro-teste@example.com",
  "password": "Password123!"
}
```

Check the HTTP response, the delivery in the dashboard, and the message's arrival in Mailpit separately. If sending succeeds, the confirmation email can be opened at `http://localhost:8025/`. The code waits for enqueue persistence, not SMTP completion; HTTP 200 does not guarantee that the email has already arrived.

Identity and enqueue do not form a single business transaction. The library has independent persistence and **at least once** delivery: an interruption after SMTP acceptance and before success is recorded may result in a duplicate. The sample lets you observe the integration, but the registration load does not prove deduplication, exactly-once delivery, or every resilience scenario. The guarantees and limitations are described in the [library README](../README.md#concurrency-leases-and-delivery-guarantees).

## Migrations

The template already contains `20260929170532_Migration20260929140514`, its designer, and `ApplicationDbContextModelSnapshot.cs`. The migration creates Identity tables and `durable_mail_deliveries`, `durable_mail_attempts`, and `durable_mail_slots`.

At WebApi startup, `Program.cs` opens a scope, resolves `ApplicationDbContext`, and executes `await context.Database.MigrateAsync()`. The database must be reachable; the configuration uses `ServerVersion.AutoDetect`. Only afterward does the application reach `await app.RunAsync()`.

EF uses `__ef_migrations_history` to determine what has already been applied. The block runs at every startup: it applies all pending compiled migrations to a new database, does not repeat recorded migrations, and applies new migrations when they are published later. It does not generate migration files. The normal first run does not require the `GenerateMigration` shortcut.

### The GenerateMigration shortcut is a reset

**This script deletes data and migration history.** Its current behavior is:

1. Read `MARIADB_NAME` and `MARIADB_USER` from `.env` and the first line of `secrets/db_password`.
2. Set the EF connection to `localhost:3306`, because the command runs on the host.
3. Delete the `Infrastructure/Migrations` folder, including the snapshot.
4. Run `mysql -u root -e "DROP DATABASE IF EXISTS ..."`.
5. Generate a new migration and run `dotnet ef database update`, using Infrastructure as the project and WebApi as the startup project.

It requires `mysql` on PATH, `dotnet-ef` compatible with EF Core 9.0.20, and an available MariaDB instance. The `mysql` invocation does not explicitly receive a host, port, or password: it uses the client's settings and **does not use** `ConnectionStrings__DefaultConnection`. Confirm that the administrative target is the intended disposable database before running it. The migration name also depends on the regional format of `%DATE%` and `%TIME%`.

For this deliberate reset, the command in `samples/sample1` is:

```bat
call ..\..\scripts\dotnet\windows\generate_migration.bat
```

The script changes the current directory to the repository root and does not restore it; in the same Command Prompt, return to `samples/sample1` before running Compose commands. After a migration change, republish and restart the WebApi so `/app` receives the same assembly versions. For incremental evolution that preserves data, use the normal EF workflow (`migrations add` and `database update`) with the host connection configured and the correct projects; do not use this reset script as an equivalent to an incremental update.

## VS Code

Open **`samples/sample1` as the workspace folder**, for example with `code .` in that directory. The profiles' `${workspaceFolder}` paths assume this root. `.vscode/settings.json` points the solution to `../../JadByte.DurableMail.slnx`, and `extensions.json` recommends C# and .NET Runtime.

In Run and Debug, the template provides:

| Profile | Action and effect |
| --- | --- |
| `DEV - JadByteDurableMailSample1.WebApi` | Runs the Debug WebApi on the host after the `build` task and opens the address announced by the server. Sets only `ASPNETCORE_ENVIRONMENT=Development`. |
| `RUN - RebuildForDocker` | Calls `buildForDocker.bat` and then restarts all Compose services. |
| `RUN - GenerateMigration` | Runs the migration/database reset described above. |
| `RUN - k6 run` | Calls the wrapper that deletes Prometheus history and starts the HTTP load. |

The `RUN` profiles invoke `cmd.exe` and are intended for Windows. The `DEV` profile does not go through the Docker entrypoint, does not automatically read the secret, and does not receive the Compose environment. Before using it, configure `ConnectionStrings__DefaultConnection` and `Smtp` for host access, using `localhost` on the published ports; avoid starting two APIs competing for the same port.

`tasks.json` provides `build`, `publish`, and `watch` for the WebApi. The regular `publish` task does not automatically include `-c Release -r linux-x64`, so it does not replace `RebuildForDocker`. The `keybindings.json` file contains shortcut preferences; its presence in `.vscode` is not equivalent to automatically installing the editor's personal keybindings.

## Queue dashboard and retries

The dashboard uses Node 22 Alpine, Express, and the MariaDB connector. The `dockerfile_inline` installs dependencies with `--omit=dev --ignore-scripts`, copies the code, and adapts the copy of `server.js` with `sed` to read `DB_HOST`, `DB_PORT`, `DB_USER`, `DB_NAME`, and `DB_PASSWORD_FILE`. The original in the ZIP retains local placeholders. This adaptation depends on the server's current snippets and must be reviewed if they change.

The service limits CPU to `0.25`, memory to `128m`, V8 heap old space to 64 MiB, the pool to two connections, and PIDs/tasks to 64. It uses the `node` user, a read-only filesystem, `/tmp` on a 16 MiB tmpfs, `no-new-privileges`, and `cap_drop: ALL`. These are configured limits, not consumption measurements or limits for the entire environment.

`package.json` still lists `sqlite3`, although the server uses MariaDB. The current template does not remove this dependency in the Dockerfile and has no `package-lock.json` in the ZIP; `npm install` resolves the declared ranges. Do not assume identical dependency builds across different dates.

| View/API | What it shows |
| --- | --- |
| Cards and chart; `/api/stats` | Total and counts by delivery state. The API also returns `avg_attempts` for successful deliveries, but the current UI does not display that average in a card. |
| Recent Failures; `/api/recent-failures` | The last ten attempts with transient or permanent failures, with expandable error details and stack traces. |
| Recent deliveries; `/api/deliveries` | The last twenty deliveries, their state, and attempt count. |

The page refreshes when opened and when **Refresh Data** is clicked; there is no automatic periodic refresh. Tailwind and Chart.js are loaded from CDNs. The dashboard queries data; it does not trigger processing or provide manual resending.

The states are `Pending`, `Processing`, `Succeeded`, `Failed`, and `Superseded`. `Pending` includes both eligible work and waiting for `next_attempt_at`. `Processing = 0` can occur between quick sends. Recent Failures contains attempt history and may show errors for a delivery that later succeeded, even with `Failed = 0`.

### Mailpit Chaos and waiting time

Compose enables:

```yaml
MP_ENABLE_CHAOS: "true"
MP_CHAOS_TRIGGERS: Sender:451:10,Recipient:451:10,Authentication:535:10
```

The last field of each trigger sets a 10% probability of failure at that SMTP stage, according to the [Mailpit Chaos documentation](https://mailpit.axllent.org/docs/integration/chaos/); it is not an overall rate of 10% of emails. The MailKit adapter classifies `451` as transient; `5xx` errors are permanent. The authentication trigger only applies when that stage occurs; the template uses empty SMTP credentials.

The sample enables scheduled retries and retains the library's other defaults: five total attempts, including the first, an initial delay of two minutes, a factor of two, and ±15% jitter. After consecutive failures, the base intervals between attempts are 2, 4, 8, and 16 minutes. A permanent failure or exhaustion of the limit marks the delivery `Failed`.

Dispatch also retains the defaults: batches of up to 25, a maximum degree of parallelism of 1 per dispatcher, and a two-minute lease. Fifty virtual users in k6 do not configure fifty simultaneous SMTP sends.

The dispatcher uses a one-shot timer for the next persisted event and rehydrates the schedule at startup. There is no need to keep the dashboard open or call the API to wake these retries. Because of the delays, the end of a roughly 100-second load test does not imply an empty queue. Deliveries and attempts can be inspected in phpMyAdmin; DurableMail timestamps are .NET UTC ticks stored as `BIGINT`, not Unix milliseconds.

`MP_DATABASE=/data/mailpit.db` persists emails in the Mailpit volume, and `MP_MAX_MESSAGES=0` disables the automatic message count limit. Successive runs accumulate data in this environment.

## Prometheus and Grafana

The WebApi instruments ASP.NET Core and the .NET runtime and exposes `/metrics`. Prometheus uses the `jadByteSample1-api` job, the internal target `webapi:8080`, and a five-second scrape interval; the global interval is fifteen seconds.

Grafana provisions the Prometheus datasource at `http://prometheus:9090` with UID `prometheus_ds`. During the build, it downloads dashboard `19924`, revision `5`, and replaces its datasource reference. The availability of that download and query compatibility with the exported metrics affect the panels.

Use `http://localhost:9090/targets` to check the target and `http://localhost:8080/metrics` to inspect the exposed metrics. In Grafana, select a time range that includes the test run. The Node dashboard's queue cards come directly from MariaDB, while Prometheus/Grafana observe HTTP and runtime; the configuration does not provide complete monitoring of every container or automatically send k6 results to Prometheus.

## Load testing with k6

The [`loadtest.js`](../scripts/k6/loadtest.js) script sends `POST http://localhost:8080/v1/signUp` with JSON, checks for HTTP 200, and waits one second between each virtual user's iterations.

| Stage | Duration | Target virtual users |
| --- | --- | --- |
| Ramp-up | 30 seconds | 50 |
| Steady load | 1 minute | 50 |
| Ramp-down | 10 seconds | 0 |

The nominal stage duration is 1 minute and 40 seconds. Fifty virtual users do not equal fifty requests per second: each waits for the HTTP response and `sleep(1)`. There is no fixed registration count.

With the `k6` binary on PATH and the API ready, the wrapper can be called **from the repository root**:

```bat
call scripts\k6\run.bat
```

Before the load test, this wrapper stops and removes the Prometheus container, **removes the `sample1_prometheus_data` volume**, and starts the service again. This deletes historical time series; it does not delete MariaDB, users, the queue, Mailpit messages, or the Grafana volume. The volume name assumes the default Compose project `sample1`. The wrapper has no explicit failure checks for these steps and does not wait for the WebApi to be ready.

To run the same load while preserving Prometheus history, run the following directly, also from the root:

```bat
k6 run scripts\k6\loadtest.js
```

Email addresses follow `teste_${__VU}_${__ITER}@example.com`, with a fixed test password. This pattern repeats when k6 is run again. Because Identity requires a unique email address, subsequent runs against the same database may reject existing registrations. Deleting Prometheus metrics does not resolve these duplicates. To compare runs, plan distinct test data or a deliberate reset of a disposable database.

The only check is HTTP 200. There are no `thresholds`, automated latency criteria, or SMTP delivery checks. Failed checks alone do not define a failure condition for the k6 process; pass/fail criteria require thresholds, according to the [k6 documentation](https://grafana.com/docs/k6/latest/using-k6/checks/). Native k6 results remain in the terminal; monitor the queue, Mailpit, logs, and HTTP/runtime charts separately.

## Persistence and inspection commands

| Compose volume | Data |
| --- | --- |
| `mariadb_data_jad_byte_durable_mail_sample1` | Database, Identity users, deliveries, attempts, and migration history. Explicit Docker name: `jad_byte_durable_mail_sample1_data`. |
| `mailpit-data` | Mailpit messages in `/data/mailpit.db`. |
| `prometheus_data` | Time series; normally `sample1_prometheus_data`. |
| `grafana_data` | Persistent Grafana state and settings. |

Volumes without an explicit name receive the Compose project prefix. `docker compose stop` stops the services; `docker compose down` removes containers/the network and preserves named volumes. The `down -v` option also removes these volumes and their data. Recreating only the containers does not mean an empty database and does not reapply migrations already recorded.

MariaDB initialization variables do not automatically reconfigure accounts in a database that already exists in the volume. Changing only `.env` or `secrets/db_password` does not change its persisted password; consult the [MariaDB image behavior](https://mariadb.com/docs/server/server-management/automated-mariadb-deployment-and-administration/docker-and-mariadb/mariadb-server-docker-official-image-environment-variables) before changing credentials in an environment with data.

Useful inspection commands, run in `samples/sample1`:

```bat
docker compose ps
docker compose logs --tail 100 webapi durable-mail-dashboard mariadb mailpit
docker compose logs -f webapi mailpit
docker stats --no-stream
curl.exe http://localhost:3005/api/stats
curl.exe http://localhost:8080/metrics
```

## Common situations

| Symptom | What to check |
| --- | --- |
| Bootstrap says the sample already exists | The condition is the existence of `samples/sample1/src`; there is no automatic repair. Examine earlier steps and preserve your files. |
| Explicit version error with a centrally managed package | The helper specifically removes OpenAPI `10.0.12`; another template/SDK may generate a different version. Check `.csproj` and `Directory.Packages.props`. |
| Restore from GitHub returns 401/403 | Check the feed, classic token, `read:packages`, token owner's username, and package access; the example mask does not authenticate. |
| Switching to packages causes an EF/Pomelo conflict | Check the consumed version's `.nuspec`; the local EF Core `0.1.0` artifact requires major version 10, which is incompatible with the template's Pomelo 9. |
| Packages restore on the host but not in the Docker build | The template does not supply NuGet configuration/authentication to the build; the running container's environment does not configure the image's restore. |
| Docker fails on a `.csproj` `COPY` | Complete generation first and keep the `../..` context; the ZIP does not contain the projects. |
| Analyzers interrupt the build only in Docker | Check that the sample's `Directory.Build.props` was copied before restore/publish, as in the provided Dockerfile. |
| New image, old behavior, or missing DLL | Check the host publication mounted at `/app`; republish with `buildForDocker.bat` and restart the WebApi. |
| WebApi fails before serving HTTP | Read the migration/connection logs; check MariaDB health, `.env`, the secret, and the target used by `ServerVersion.AutoDetect`. |
| Changing the password file does not work with an old volume | Credentials already initialized in MariaDB persist in the database; editing the file does not automatically change existing accounts. |
| Dashboard appears stuck on Pending | Click Refresh Data and check Chaos, attempt count, and `next_attempt_at`; Pending may represent scheduled waiting. |
| Dashboard returns an error immediately at startup | A healthy database does not guarantee that the WebApi has already created the tables; wait for initialization and check both services' logs. |
| k6 fails on the second test | Check for duplicate emails and API readiness; the wrapper does not clear users. |
| Grafana has no data | Check `/metrics`, the Prometheus target, datasource, selected time range, and the history reset performed by the k6 wrapper. Not every imported dashboard query necessarily matches the available time series. |
| Local debugging fails, but the container works | The DEV profile does not inherit the Compose connection/SMTP configuration; configure the host .NET process's environment. |

## Verification scope of this guide

The content was checked by reading the scripts, ZIP, solution, current library code, and local package metadata. The ZIP's Compose file was compared with the supplied attachment and analyzed by the Docker Compose parser without resolving paths, interpolating the environment, or requiring files that had not yet been extracted. This verification confirms the declared structure of the seven services; it does not replace a startup test.

During preparation of the documentation, no bootstrap, publish, migrations, k6 load tests, or container startup/recreation were run. GitHub publication was reported by the author: there was no feed authentication, remote package download, or push to verify the published version. The library's automated checks and their historical results are in [implementation-status.md](implementation-status.md); that historical record is not a current inventory of publications. This manual integration environment complements those checks.
