@ECHO OFF

cd /d "%~dp0..\..\.." || EXIT /B 1

if not exist "samples/sample1/src" (
   echo "Sample1 not found."
   exit /b 0
)

:: samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application package AutoMapper --version 16.2.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application package FluentValidation --version 12.1.1
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application package FluentValidation.DependencyInjectionExtensions --version 12.1.1
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application package MediatR --version 14.2.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application package Microsoft.Extensions.Logging --version 10.0.12

:: samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure package MailKit --version 4.18.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure package Pomelo.EntityFrameworkCore.MySql --version 9.0.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure package Microsoft.AspNetCore.Identity.EntityFrameworkCore --version 9.0.20
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure package EFCore.NamingConventions --version 9.0.0

:: samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package Scalar.AspNetCore --version 2.17.8
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package Swashbuckle.AspNetCore.SwaggerUI --version 10.2.3
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package AutoMapper --version 16.2.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package MediatR --version 14.2.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package Microsoft.EntityFrameworkCore.Design --version 9.0.20
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package OpenTelemetry.Extensions.Hosting --version 1.19.1
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package OpenTelemetry.Instrumentation.AspNetCore --version 1.19.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package OpenTelemetry.Instrumentation.Runtime --version 1.19.0
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi package OpenTelemetry.Exporter.Prometheus.AspNetCore --version 1.19.1-beta.1
