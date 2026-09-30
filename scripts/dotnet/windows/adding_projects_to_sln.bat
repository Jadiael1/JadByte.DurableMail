@ECHO OFF

cd /d "%~dp0..\..\.." || EXIT /B 1

set "SOLUTION=JadByte.DurableMail.slnx"

if not exist "%SOLUTION%" (
   echo "Solution not found."
   exit /b 0
)

findstr /C:"samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Domain/JadByte.DurableMail.Samples.Sample1.Domain.csproj" "%SOLUTION%" >nul
if errorlevel 1 (
    dotnet sln %SOLUTION% add ^
        samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Domain/JadByte.DurableMail.Samples.Sample1.Domain.csproj
)

findstr /C:"samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Application/JadByte.DurableMail.Samples.Sample1.Application.csproj" "%SOLUTION%" >nul
if errorlevel 1 (
    dotnet sln %SOLUTION% add ^
        samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Application/JadByte.DurableMail.Samples.Sample1.Application.csproj
)

findstr /C:"samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Infrastructure/JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj" "%SOLUTION%" >nul
if errorlevel 1 (
    dotnet sln %SOLUTION% add ^
        samples/sample1/src/JadByte.DurableMail.Samples.Sample1.Infrastructure/JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj
)

findstr /C:"samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj" "%SOLUTION%" >nul
if errorlevel 1 (
    dotnet sln %SOLUTION% add ^
        samples/sample1/src/JadByte.DurableMail.Samples.Sample1.WebApi/JadByte.DurableMail.Samples.Sample1.WebApi.csproj
)
