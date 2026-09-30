@ECHO OFF
SETLOCAL DisableDelayedExpansion

cd /d "%~dp0..\..\.." || EXIT /B 1

set "ENV_FILE=samples\sample1\.env"
set "PASSWORD_FILE=samples\sample1\secrets\db_password"

if not exist "%ENV_FILE%" (
    >&2 echo File not found: %ENV_FILE%
    EXIT /B 1
)

if not exist "%PASSWORD_FILE%" (
    >&2 echo File not found: %PASSWORD_FILE%
    EXIT /B 1
)

set "MARIADB_NAME="
set "MARIADB_USER="
set "MARIADB_PASSWORD="

for /F "usebackq eol=# tokens=1,* delims==" %%A in ("%ENV_FILE%") do (
    if /I "%%A"=="MARIADB_NAME" set "MARIADB_NAME=%%~B"
    if /I "%%A"=="MARIADB_USER" set "MARIADB_USER=%%~B"
)

set /P "MARIADB_PASSWORD="<"%PASSWORD_FILE%"

if not defined MARIADB_NAME (
    >&2 echo MARIADB_NAME was not defined in %ENV_FILE%
    EXIT /B 1
)

if not defined MARIADB_USER (
    >&2 echo MARIADB_USER was not defined in %ENV_FILE%
    EXIT /B 1
)

if not defined MARIADB_PASSWORD (
    >&2 echo The MariaDB password is empty in %PASSWORD_FILE%
    EXIT /B 1
)

set "ConnectionStrings__DefaultConnection=Server=localhost;Port=3306;Database=%MARIADB_NAME%;User Id=%MARIADB_USER%;Password=%MARIADB_PASSWORD%;TreatTinyAsBoolean=false;"

if exist samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\Migrations (
    rmdir /S /Q samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\Migrations || EXIT /B 1
)

mysql -u root -e "DROP DATABASE IF EXISTS `%MARIADB_NAME%`;" || EXIT /B 1

set "T=%TIME: =0%"
set "name=Migration%DATE:~-4%%DATE:~3,2%%DATE:~0,2%%T:~0,2%%T:~3,2%%T:~6,2%"
set "ASPNETCORE_ENVIRONMENT=Development"

dotnet ef migrations add "%name%" --project samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure --startup-project samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi || EXIT /B 1
dotnet ef database update --project samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure --startup-project samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi || EXIT /B 1
