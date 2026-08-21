@echo off
setlocal enabledelayedexpansion
if exist "%~dp0..\FloatQuote.csproj" (
    set "APP_DIR=%~dp0.."
) else (
    set "APP_DIR=D:\development\FloatQuote.Win"
)
set "PID_FILE=%APP_DIR%\.floatquote.pid"

if exist "%PID_FILE%" (
    set /p OLD_PID=<"%PID_FILE%"
    tasklist /FI "PID eq !OLD_PID!" 2>nul | findstr /C:"!OLD_PID!" >nul
    if not errorlevel 1 (
        echo FloatQuote is already running (PID: !OLD_PID!)
        timeout /t 2 >nul
        exit /b 0
    )
    del "%PID_FILE%" >nul 2>&1
)

set "EXE="
if exist "%APP_DIR%\publish\FloatQuote.exe" set "EXE=%APP_DIR%\publish\FloatQuote.exe"
if not defined EXE if exist "%APP_DIR%\bin\Release\net8.0-windows\FloatQuote.exe" set "EXE=%APP_DIR%\bin\Release\net8.0-windows\FloatQuote.exe"
if not defined EXE if exist "%APP_DIR%\bin\Debug\net8.0-windows\FloatQuote.exe" set "EXE=%APP_DIR%\bin\Debug\net8.0-windows\FloatQuote.exe"

if defined EXE (
    start "" "%EXE%"
) else (
    where dotnet >nul 2>&1 || (
        echo dotnet SDK not found. Build the project first.
        timeout /t 3 >nul
        exit /b 1
    )
    start "" dotnet run --project "%APP_DIR%\FloatQuote.csproj" -c Release --no-build 2>nul
    if errorlevel 1 start "" dotnet run --project "%APP_DIR%\FloatQuote.csproj" -c Release
)

echo FloatQuote started.
timeout /t 2 >nul
