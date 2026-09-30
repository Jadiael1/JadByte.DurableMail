@echo off
setlocal DisableDelayedExpansion

cd /d "%~dp0..\..\..\.." || EXIT /B 1

set "ARQUIVO=samples\sample1\src\JadByte.DurableMail.Samples.Sample1.WebApi\JadByte.DurableMail.Samples.Sample1.WebApi.csproj"
set "TEMP_FILE=%ARQUIVO%.tmp"

echo Processando o arquivo via CMD puro...

rem Cria/limpa o arquivo temporário
type nul > "%TEMP_FILE%"

rem O findstr /n "^" garante que as linhas em branco não sumam durante o FOR /F
for /f "delims=" %%A in ('findstr /n "^" "%ARQUIVO%"') do (
    set "linha=%%A"

    rem Ativar expansão atrasada apenas dentro do loop protege caracteres XML especiais (<, >) e exclamações (!)
    setlocal EnableDelayedExpansion

    rem Remove o prefixo do número da linha adicionado pelo findstr (ex: "14:")
    set "linha=!linha:*:=!"

    if defined linha (
        rem Verifica se a linha atual contém o nome do pacote e a versão
        if "!linha!" neq "!linha:Microsoft.AspNetCore.OpenApi=!" (
            if "!linha!" neq "!linha:10.0.12=!" (
                rem Reescreve a linha removendo o Version, mantendo os 4 espaços de recuo
                set "linha=    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />"
            )
        )
        rem Grava a linha de volta no arquivo temporário
        echo(!linha!>>"%TEMP_FILE%"
    ) else (
        rem Grava a linha em branco
        echo(>>"%TEMP_FILE%"
    )

    endlocal
)

rem Substitui o arquivo original pelo arquivo modificado
move /y "%TEMP_FILE%" "%ARQUIVO%" >nul

echo Concluido!
