@ECHO OFF
SETLOCAL EnableExtensions DisableDelayedExpansion

cd /d "%~dp0..\..\..\.." || EXIT /B 1

SET "PROJECT=samples\sample1\src\JadByte.DurableMail.Samples.Sample1.Infrastructure\JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj"
SET "TEMP_FILE=%PROJECT%.tmp"

if not exist "%PROJECT%" (
   echo "JadByte.DurableMail.Samples.Sample1.Infrastructure.csproj not found."
   exit /b 0
)

IF NOT EXIST "%PROJECT%" (
    ECHO [ERROR] Project not found: %PROJECT%
    EXIT /B 1
)

REM Garante idempotencia.
FINDSTR /L /I /C:"FrameworkReference" "%PROJECT%" | FINDSTR /L /I /C:"Microsoft.AspNetCore.App" >NUL
IF NOT ERRORLEVEL 1 (
    ECHO [INFO] Microsoft.AspNetCore.App FrameworkReference already exists.
    EXIT /B 0
)

REM Impede confundir FrameworkReference com o pacote NuGet legado.
FINDSTR /L /I /C:"PackageReference" "%PROJECT%" | FINDSTR /L /I /C:"Microsoft.AspNetCore.App" >NUL
IF NOT ERRORLEVEL 1 (
    ECHO [ERROR] Microsoft.AspNetCore.App was found as PackageReference.
    ECHO [ERROR] Remove the legacy PackageReference before continuing.
    EXIT /B 1
)

REM Procura por </Project> ignorando espaços ou CRLF no fim
FINDSTR /R /C:"</Project>" "%PROJECT%" >NUL
IF ERRORLEVEL 1 (
    ECHO [ERROR] Closing ^</Project^> tag was not found in the expected format.
    EXIT /B 1
)

DEL /Q "%TEMP_FILE%" 2>NUL

REM Filtra invertendo (/V) para remover a tag </Project>
FINDSTR /R /V /C:"</Project>" "%PROJECT%" > "%TEMP_FILE%"
IF ERRORLEVEL 1 (
    ECHO [ERROR] Could not create temporary project file.
    DEL /Q "%TEMP_FILE%" 2>NUL
    EXIT /B 1
)

REM Adiciona FrameworkReference antes de fechar Project.
>>"%TEMP_FILE%" ECHO.
>>"%TEMP_FILE%" ECHO   ^<ItemGroup^>
>>"%TEMP_FILE%" ECHO     ^<FrameworkReference Include="Microsoft.AspNetCore.App" /^>
>>"%TEMP_FILE%" ECHO   ^</ItemGroup^>
>>"%TEMP_FILE%" ECHO.
>>"%TEMP_FILE%" ECHO ^</Project^>

REM Valida o resultado antes de substituir o arquivo original.
FINDSTR /L /I /C:"FrameworkReference" "%TEMP_FILE%" | FINDSTR /L /I /C:"Microsoft.AspNetCore.App" >NUL
IF ERRORLEVEL 1 (
    ECHO [ERROR] FrameworkReference validation failed.
    DEL /Q "%TEMP_FILE%" 2>NUL
    EXIT /B 1
)

MOVE /Y "%TEMP_FILE%" "%PROJECT%" >NUL
IF ERRORLEVEL 1 (
    ECHO [ERROR] Could not update project file.
    EXIT /B 1
)

ECHO [OK] Microsoft.AspNetCore.App FrameworkReference added successfully.

ENDLOCAL
EXIT /B 0
