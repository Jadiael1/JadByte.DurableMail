@echo off
cd /d "%~dp0..\..\..\.." || EXIT /B 1

set "PASTA=samples\sample1"
set "ARQUIVO=%PASTA%\Directory.Packages.props"

if not exist "%PASTA%\" mkdir "%PASTA%"

(
    echo ^<Project^>
    echo   ^<PropertyGroup^>
    echo     ^<ManagePackageVersionsCentrally^>true^</ManagePackageVersionsCentrally^>
    echo   ^</PropertyGroup^>
    echo   ^<ItemGroup^>
    echo     ^<!-- samples\sample1 --^>
    echo     ^<PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.12" /^>
    echo     ^<PackageVersion Include="Scalar.AspNetCore" version="2.17.8" /^>
    echo     ^<PackageVersion Include="Swashbuckle.AspNetCore.SwaggerUI" version="10.2.3" /^>
    echo     ^<PackageVersion Include="AutoMapper" version="16.2.0" /^>
    echo     ^<PackageVersion Include="MediatR" version="14.2.0" /^>
    echo     ^<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" version="9.0.20" /^>
    echo     ^<PackageVersion Include="OpenTelemetry.Extensions.Hosting" version="1.19.1" /^>
    echo     ^<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" version="1.19.0" /^>
    echo     ^<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" version="1.19.0" /^>
    echo     ^<PackageVersion Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" version="1.19.1-beta.1" /^>
    echo     ^<PackageVersion Include="FluentValidation" version="12.1.1" /^>
    echo     ^<PackageVersion Include="FluentValidation.DependencyInjectionExtensions" version="12.1.1" /^>
    echo     ^<PackageVersion Include="Microsoft.Extensions.Logging" version="10.0.12" /^>
    echo     ^<PackageVersion Include="MailKit" version="4.18.0" /^>
    echo     ^<PackageVersion Include="Pomelo.EntityFrameworkCore.MySql" version="9.0.0" /^>
    echo     ^<PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" version="9.0.20" /^>
    echo     ^<PackageVersion Include="EFCore.NamingConventions" version="9.0.0" /^>
    echo     ^</ItemGroup^>
    echo ^</Project^>
) > "%ARQUIVO%"
