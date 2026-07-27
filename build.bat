@echo off
setlocal

pushd "%~dp0"

set "CONFIGURATION=%~1"
if not defined CONFIGURATION set "CONFIGURATION=Debug"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK tidak ditemukan. Pastikan dotnet tersedia di PATH.
    popd
    exit /b 1
)

echo Restoring MiniSSMS...
dotnet restore "SSMS.sln"
if errorlevel 1 goto :build_failed

echo Building MiniSSMS - %CONFIGURATION%...
dotnet build "SSMS.sln" --configuration "%CONFIGURATION%" --no-restore
if errorlevel 1 goto :build_failed

echo.
echo Build berhasil.
echo Output: bin\%CONFIGURATION%\net9.0-windows\
popd
exit /b 0

:build_failed
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
echo.
echo [ERROR] Build gagal dengan exit code %BUILD_EXIT_CODE%.
popd
exit /b %BUILD_EXIT_CODE%
