@echo off
echo.
echo   GamePinned v3.0 - Multi-Platform Game Protection
echo   =================================================
echo.

:: Check for admin rights
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo   [ERROR] Run as Administrator!
    pause
    exit /b 1
)

:: Check .NET SDK
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo   [ERROR] .NET 10.0 SDK required!
    echo   Download: https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

echo   What would you like to build?
echo.
echo   [1] Service only (console/service mode)
echo   [2] GUI only (settings panel)
echo   [3] Both Service + GUI (recommended)
echo.
set /p buildChoice="  Choice [3]: "
if "%buildChoice%"=="" set buildChoice=3

if "%buildChoice%"=="1" goto :buildService
if "%buildChoice%"=="2" goto :buildGUI
if "%buildChoice%"=="3" goto :buildBoth
echo   Invalid choice.
pause
exit /b 1

:buildService
echo.
echo   Building GamePinned Service (Native AOT)...
cd /d "%~dp0GamePinnedService"
dotnet publish -c Release -r win-x64 -o "%~dp0publish\service"
if %errorLevel% neq 0 (
    echo   [ERROR] Service build failed!
    pause
    exit /b 1
)
echo   Service build complete!
goto :runMenu

:buildGUI
echo.
echo   Building GamePinned GUI...
cd /d "%~dp0GamePinnedGUI"
dotnet publish -c Release -r win-x64 -o "%~dp0publish\gui" --self-contained true
if %errorLevel% neq 0 (
    echo   [ERROR] GUI build failed!
    pause
    exit /b 1
)
echo   GUI build complete!
goto :runMenu

:buildBoth
echo.
echo   Building GamePinned Service...
cd /d "%~dp0GamePinnedService"
dotnet publish -c Release -r win-x64 -o "%~dp0publish\service"
if %errorLevel% neq 0 (
    echo   [ERROR] Service build failed!
    pause
    exit /b 1
)
echo   Service build complete!

echo.
echo   Building GamePinned GUI...
cd /d "%~dp0GamePinnedGUI"
dotnet publish -c Release -r win-x64 -o "%~dp0publish\gui" --self-contained true
if %errorLevel% neq 0 (
    echo   [ERROR] GUI build failed!
    pause
    exit /b 1
)
echo   GUI build complete!
goto :runMenu

:runMenu
echo.
echo   ==========================================
echo   What would you like to do?
echo   ==========================================
echo.
echo   [1] Run Service as Console App (testing)
echo   [2] Install Service as Windows Service (production)
echo   [3] Launch GUI Settings Panel
echo   [4] Install Service + Launch GUI
echo   [5] Exit
echo.
set /p runChoice="  Choice: "

if "%runChoice%"=="1" (
    echo.
    cd /d "%~dp0publish\service"
    GamePinnedService.exe
)
if "%runChoice%"=="2" (
    cd /d "%~dp0publish\service"
    GamePinnedService.exe --install-service
    pause
)
if "%runChoice%"=="3" (
    echo   Launching GUI...
    start "" "%~dp0publish\gui\GamePinned.exe"
)
if "%runChoice%"=="4" (
    echo   Installing service...
    cd /d "%~dp0publish\service"
    GamePinnedService.exe --install-service
    echo   Launching GUI...
    start "" "%~dp0publish\gui\GamePinned.exe"
    pause
)
if "%runChoice%"=="5" (
    exit /b 0
)
