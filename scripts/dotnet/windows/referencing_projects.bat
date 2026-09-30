@ECHO OFF

cd /d "%~dp0..\..\.." || EXIT /B 1

if not exist "samples/sample1/src" (
   echo "Sample1 not found."
   exit /b 0
)

::             ┌──────────────────┐
::             │      Domain      │
::             └─▲──────────────▲─┘
::               │              │
::   ┌───────────┘              └───────────┐
::   │                                      │
::   │                              ┌───────┴───────┐
::   │                              │  Application  │
::   │                              └───────▲───────┘
::   │                                      │ ▲
::   │                                      │ │
:: ┌─┴──────────────┐                       │ │
:: │ Infrastructure │───────────────────────┘ │
:: └─▲──────────────┘                         │
::   │                                        │
::   │                 ┌────────────┐         │
::   └─────────────────┤   WebApi   ├─────────┘
::                     └────────────┘

:: 1. Adiciona Domain a Application
dotnet add samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Application/JadByte.DurableMail.Samples.Sample1.Application.csproj reference samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Domain/JadByte.DurableMail.Samples.Sample1.Domain.csproj
:: 2. Adiciona Domain a Infrastructure
dotnet add samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Infrastructure/JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj reference samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Domain/JadByte.DurableMail.Samples.Sample1.Domain.csproj
:: 3. Adiciona Application a Infrastructure
dotnet add samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Infrastructure/JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj reference samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Application/JadByte.DurableMail.Samples.Sample1.Application.csproj
:: 4. Adiciona Application a Presentation
dotnet add samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj reference samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Application/JadByte.DurableMail.Samples.Sample1.Application.csproj
:: 5. Adiciona Infrastructure a Presentation
dotnet add samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj reference samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Infrastructure/JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj



:: 6. Adiciona JadByte.DurableMail.EntityFrameworkCore a JadByte.DurableMail.Samples.Sample1.Infrastructure
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj reference src\JadByte.DurableMail.EntityFrameworkCore\JadByte.DurableMail.EntityFrameworkCore.csproj
:: 7. Adiciona JadByte.DurableMail.MailKit a JadByte.DurableMail.Samples.Sample1.Infrastructure
dotnet add samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj reference src\JadByte.DurableMail.MailKit\JadByte.DurableMail.MailKit.csproj

call scripts\dotnet\windows\inc\add_framework_reference.bat
