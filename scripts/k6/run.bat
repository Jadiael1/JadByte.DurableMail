@echo off
setlocal

pushd "%cd%"

if not exist "%~dp0..\..\samples\sample1" (
    echo "Sample1 not found."
    exit /b 0
)

cd /d "%~dp0..\..\samples\sample1" || EXIT /B 1

:: Reset métricas Grafana.
docker compose stop prometheus
docker compose rm -f prometheus
docker volume rm sample1_prometheus_data
docker compose up -d prometheus

k6 run ..\..\scripts\k6\loadtest.js

popd
endlocal
