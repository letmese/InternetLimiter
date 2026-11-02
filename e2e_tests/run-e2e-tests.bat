@echo off
setlocal
set SCRIPT_DIR=%~dp0
set PROJECT=%SCRIPT_DIR%Valve.E2ETests.csproj

echo ================================================================================
echo                 VALVE E2E TEST RUNNER LAUNCHER                                 
echo ================================================================================

dotnet build "%PROJECT%" -c Debug
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed!
    exit /b %ERRORLEVEL%
)

dotnet run --project "%PROJECT%" --no-build -c Debug -- %*
exit /b %ERRORLEVEL%
