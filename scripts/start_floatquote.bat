@echo off
setlocal enabledelayedexpansion
rem Locate project dir: if this script lives in <repo>\scripts, use parent; otherwise fixed path
if exist "%~dp0..\main.py" (
    set "APP_DIR=%~dp0.."
) else (
    set "APP_DIR=D:\development\FloatQuote"
)
set "PID_FILE=%APP_DIR%\.floatquote.pid"
set "PYW=C:\Users\rkkkgy\AppData\Local\Programs\Python\Python311\pythonw.exe"

rem Already running? then exit
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

rem Prefer pythonw, fall back to python on PATH
if not exist "%PYW%" (
    where pythonw >nul 2>&1 && set "PYW=pythonw"
    if errorlevel 1 where python >nul 2>&1 && set "PYW=python"
)

start "" "%PYW%" "%APP_DIR%\main.py"
echo FloatQuote started.
timeout /t 2 >nul