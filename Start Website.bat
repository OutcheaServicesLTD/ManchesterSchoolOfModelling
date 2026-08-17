@echo off
REM ============================================================================
REM  Manchester School of Modelling — start the website
REM
REM  Double-click this file. It starts the site and opens it in your browser.
REM  To stop the site, close the black window this opens.
REM
REM  Written for someone who does not want to use a terminal: it checks what is
REM  installed, says plainly what is missing, and opens the browser only once the
REM  site is actually ready rather than a moment too early.
REM ============================================================================

title Manchester School of Modelling - website
cd /d "%~dp0"

echo.
echo   MANCHESTER SCHOOL OF MODELLING
echo   ------------------------------
echo.

REM --- Is .NET installed at all? --------------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
    echo   .NET is not installed on this computer.
    echo.
    echo   A download page will now open. Choose the button that says
    echo   "SDK" -- NOT the one that says "Runtime". Install it, restart
    echo   this computer, then double-click this file again.
    echo.
    start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
    pause
    exit /b 1
)

REM --- Is it the SDK, or only the runtime? ----------------------------------
REM  "dotnet run" needs the SDK. The runtime alone can start a finished
REM  application but cannot build one, and the download page offers both.
dotnet --list-sdks >nul 2>&1
if errorlevel 1 goto :nosdk

for /f %%s in ('dotnet --list-sdks 2^>nul ^| find /c "."') do set SDKCOUNT=%%s
if "%SDKCOUNT%"=="0" goto :nosdk

echo   Starting the website. The first time takes about a minute.
echo   Please leave this window open while you use the site.
echo.

REM --- Open the browser once the site is up ---------------------------------
REM  Runs alongside the site rather than before it, so the browser does not
REM  arrive at a port nothing is answering on yet.
start "" cmd /c "timeout /t 30 /nobreak >nul && start "" http://localhost:5213/models"

REM  Development mode: this is a local preview, so the sample sign-in account
REM  is created and the placeholder payment and email services are used.
set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://localhost:5213

dotnet run --project "src\Msm.Portfolio.Web"

echo.
echo   The website has stopped.
pause
exit /b 0

:nosdk
echo   .NET is installed, but only the part that runs finished programs.
echo   This project also needs the "SDK" to build itself.
echo.
echo   A download page will now open. Choose the button that says
echo   "SDK" -- NOT the one that says "Runtime". Install it, restart
echo   this computer, then double-click this file again.
echo.
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
pause
exit /b 1
