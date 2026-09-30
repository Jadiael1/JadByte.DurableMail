@ECHO OFF

cd /d "%~dp0..\..\.." || EXIT /B 1

if exist "samples/sample1/src" (
   echo "Sample1 already exist."
   exit /b 0
)

if not exist "samples" (
    mkdir "samples"
)
if not exist "samples/sample1" (
    mkdir "samples/sample1"
)
if not exist "samples/sample1/src" (
    mkdir "samples/sample1/src"
)

cd samples/sample1/src
dotnet new classlib -n JadByte.DurableMail.Samples.Sample1.Domain
dotnet new classlib -n JadByte.DurableMail.Samples.Sample1.Application
dotnet new classlib -n JadByte.DurableMail.Samples.Sample1.Infrastructure
dotnet new webapi -n JadByte.DurableMail.Samples.Sample1.WebApi --no-restore
cd /d "%~dp0..\..\.."

del /f /q "samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Application\Class1.cs"
del /f /q "samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Domain\Class1.cs"
del /f /q "samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\Class1.cs"

call scripts\dotnet\windows\inc\remove_version.bat
call scripts\dotnet\windows\inc\create_props.bat
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\adding_projects_to_sln.bat
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\adding_projects_to_sln.bat
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\referencing_projects.bat
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\referencing_projects.bat
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\add_package_to_project.bat
timeout /t 3 /nobreak >nul
tar -xf "scripts\dotnet\sample1.zip" -C "samples\sample1"
timeout /t 3 /nobreak >nul
call scripts\dotnet\windows\buildForDocker.bat
@REM  timeout /t 3 /nobreak >nul
@REM  call scripts\dotnet\windows\generate_migration.bat
