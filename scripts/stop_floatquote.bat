@echo off
setlocal
if exist "%~dp0..\main.py" (
    set "APP_DIR=%~dp0.."
) else (
    set "APP_DIR=D:\development\FloatQuote"
)
set "PID_FILE=%APP_DIR%\.floatquote.pid"

if not exist "%PID_FILE%" (
    echo PID file not found. FloatQuote may not be running.
    timeout /t 2 >nul
    exit /b 0
)
set /p PID=<"%PID_FILE%"
taskkill /PID %PID% /F >nul 2>&1
if errorlevel 1 (
    echo Process %PID% not found or already exited.
) else (
    echo FloatQuote stopped.
)
del "%PID_FILE%" >nul 2>&1
timeout /t 2 >nul