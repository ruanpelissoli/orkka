@echo off
rem orkka.cmd - run Orkka from this clone (Windows; the same steps as ./orkka). Readable on
rem purpose: it checks your tools, builds the UI when its sources changed, builds the .NET app,
rem then runs it. It installs nothing and writes only inside this folder and Orkka's data
rem folder (%USERPROFILE%\.orkka, or %ORKKA_HOME%). Plain cmd, so no PowerShell policy prompt.
setlocal EnableExtensions
cd /d "%~dp0"
set "UI_DIST=ui\apps\local\dist"
set "APP=src\Orkka\bin\Release\net10.0\orkka.dll"

rem 1. The .NET SDK that global.json asks for (dotnet refuses to run without it).
where dotnet >nul 2>nul || (echo Orkka needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0 & exit /b 1)
dotnet --version >nul 2>nul || (echo Your .NET SDK does not match global.json ^(needs 10.0.300 or newer^): https://dotnet.microsoft.com/download/dotnet/10.0 & exit /b 1)

rem 2. The web UI, rebuilt only when ui\ changed (committed or not) since the last build.
rem    The stamp is a hash of ui\'s tracked files, uncommitted edits and new files.
if /i not "%~1"=="start" goto app
if defined ORKKA_SKIP_UI_BUILD goto app
set "STAMP=no-git"
for /f %%h in ('"(git ls-files -s -- ui & git diff --no-ext-diff -- ui & git ls-files -o --exclude-standard -- ui) 2>nul | git hash-object --stdin"') do set "STAMP=%%h"
set "OLD="
if exist "%UI_DIST%\.orkka-build-stamp" set /p OLD=<"%UI_DIST%\.orkka-build-stamp"
if exist "%UI_DIST%\index.html" if "%OLD%"=="%STAMP%" goto app
where node >nul 2>nul || (echo Building the UI needs Node.js 20 or newer with npm: https://nodejs.org/ & exit /b 1)
for /f %%v in ('node -p "process.versions.node.split('.')[0]"') do set "NODE_MAJOR=%%v"
if %NODE_MAJOR% LSS 20 (echo Node.js 20 or newer is needed: https://nodejs.org/ & exit /b 1)
rem    npm ci only when package-lock.json changed (its stamp lives in node_modules).
for /f %%h in ('git hash-object ui\package-lock.json') do set "LOCK=%%h"
set "OLDLOCK="
if exist "ui\node_modules\.orkka-lock-stamp" set /p OLDLOCK=<"ui\node_modules\.orkka-lock-stamp"
if "%OLDLOCK%"=="%LOCK%" goto build_ui
echo Installing the UI's packages (npm ci)...
pushd ui
call npm ci --no-audit --no-fund || (popd & echo npm ci failed - see above. & exit /b 1)
popd
>"ui\node_modules\.orkka-lock-stamp" echo %LOCK%
:build_ui
echo Building the UI (npm run build)...
pushd ui
call npm run build || (popd & echo The UI build failed - see above. & exit /b 1)
popd
if exist "%UI_DIST%\index.html" goto ui_built
echo The UI build did not produce %UI_DIST%\index.html - see above.
exit /b 1
:ui_built
>"%UI_DIST%\.orkka-build-stamp" echo %STAMP%

:app
rem 3. The app: built for `start` (a no-op when nothing changed), otherwise only when missing,
rem    so `orkka status` and friends answer at once. Then run it.
rem    The build leaves no MSBuild or compiler server running (no node reuse, no shared compilation).
set "BUILD="
if /i "%~1"=="start" set "BUILD=1"
if not exist "%APP%" set "BUILD=1"
if not defined BUILD goto run
dotnet build src\Orkka -c Release --nologo -v q -clp:ErrorsOnly -nodeReuse:false -p:UseSharedCompilation=false || (echo The build failed. If Orkka is already running, stop it first: orkka stop & exit /b 1)
:run
dotnet "%APP%" %*
exit /b %ERRORLEVEL%
