@ECHO OFF
setlocal

pushd "%cd%"

if not exist "%~dp0..\..\..\samples\sample1" (
    echo "Sample1 not found."
    exit /b 1
)

cd /d "%~dp0..\..\.." || EXIT /B 1

@REM  dotnet clean JadByte.DurableMail.slnx
@REM  if errorlevel 1 exit /b %errorlevel%

@REM  dotnet nuget locals all --clear
if errorlevel 1 exit /b %errorlevel%

for /f "tokens=*" %%A in ('findstr /b /c:"dotnet publish " scripts\dotnet\linux\publish.sh') do (
    %%A
    if errorlevel 1 exit /b %errorlevel%
)

cd /d "%~dp0..\..\..\samples\sample1" || EXIT /B 1

popd
endlocal
