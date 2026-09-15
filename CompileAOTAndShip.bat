@echo off
REM ============================================================================
REM  CompileAOTAndShip.bat — Native AOT build of the Azur Promilia plugin.
REM
REM  Produces a self-contained, single-architecture (win-x64) AOT binary in
REM  Hi3Helper.Plugin.Azurpromilia\publish\<Configuration>\AzurPromilia.dll
REM
REM  Requires: .NET 10 SDK + the "Desktop development with C++" workload
REM            (MSVC linker) for the Native AOT toolchain.
REM
REM  Usage:
REM      CompileAOTAndShip.bat <1..6>      non-interactive
REM      CompileAOTAndShip.bat             interactive menu
REM ============================================================================

setlocal

set _pluginProject=Hi3Helper.Plugin.Azurpromilia
set _assemblyName=AzurPromilia
set _isRemoveSymbol=true

set currentPath=%~dp0
set projectPath=%currentPath%%_pluginProject%
set projectPublishPath=%projectPath%\publish%
set solutionPath=%currentPath%Hi3Helper.Plugin.Azurpromilia.sln

REM Optional: path to the Collapse plugin catalog Indexer tool. When present, the
REM publish output is post-processed into a shippable manifest.json + zip.
set indexerToolPath=%currentPath%Indexer.exe

set args1=%1

cd /d "%currentPath%"

:CompileSpeedPreference
set speedChoice=%args1%
if "%speedChoice%" == "" (
    echo Please define which optimization to use:
    echo   1. Size ^(Native AOT, reflection enabled^)
    echo   2. Speed ^(Native AOT, reflection enabled^)
    echo   3. Debug ^(Native AOT, no optimization^)
    echo   4. Size-ReflectionFree ^(experimental^)
    echo   5. Speed-ReflectionFree ^(experimental^)
    echo   6. Debug-ReflectionFree ^(experimental^)
    echo.
    echo Note:
    echo ReflectionFree builds are EXPERIMENTAL and may behave unexpectedly.
    echo Some reflection-dependent features might not work.
    echo.
    set /p speedChoice=Choice^?^> 
)

if "%speedChoice%" == "1" (
  set publishProfile=ReleasePublish-O1
  set configuration=Release
  goto :StartCompilation
) else if "%speedChoice%" == "2" (
  set publishProfile=ReleasePublish-O2
  set configuration=Release
  goto :StartCompilation
) else if "%speedChoice%" == "3" (
  set publishProfile=DebugPublish
  set configuration=Debug
  set _isRemoveSymbol=false
  goto :StartCompilation
) else if "%speedChoice%" == "4" (
  set publishProfile=ReleaseNoReflectionPublish-O1
  set configuration=ReleaseNoReflection
  goto :StartCompilation
) else if "%speedChoice%" == "5" (
  set publishProfile=ReleaseNoReflectionPublish-O2
  set configuration=ReleaseNoReflection
  goto :StartCompilation
) else if "%speedChoice%" == "6" (
  set publishProfile=DebugNoReflectionPublish
  set configuration=DebugNoReflection
  set _isRemoveSymbol=false
  goto :StartCompilation
)

cls
echo Input is not valid! Available choices: 1, 2, 3, 4, 5 or 6
set publishProfile=
set args1=
goto :CompileSpeedPreference

:StartCompilation
set outputBaseDirPath=%projectPublishPath%%configuration%
set outputDirPath=%outputBaseDirPath%

if /I exist "%projectPublishPath%" (
    rmdir /S /Q "%projectPublishPath%" || goto :Error
)
mkdir "%outputDirPath%"
cd /d "%projectPath%"

dotnet restore --runtime win-x64 "%solutionPath%" || goto :Error
dotnet clean --configuration %configuration% --runtime win-x64 "%solutionPath%" || goto :Error
dotnet publish "%_pluginProject%.csproj" --configuration %configuration% --runtime win-x64 /p:PublishProfile=%publishProfile% -o "%outputDirPath%" || goto :Error

:RemovePDBIfNotDebug
if "%_isRemoveSymbol%" == "true" (
  del "%outputDirPath%\*.pdb" 2>nul
)

:StartIndexing
if exist "%indexerToolPath%" (
    "%indexerToolPath%" "%outputDirPath%" || goto :Error
) else (
    echo.
    echo [skip] Indexer.exe was not found at "%indexerToolPath%".
    echo        The publish output is complete, but manifest.json was not generated.
    echo        Point indexerToolPath at the Collapse plugin Indexer to package a release.
)

goto :CompileSuccess

:Error
echo An error has occurred while performing compilation with error status: %errorlevel%
goto :End

:CompileSuccess
echo.
echo The plugin has been compiled successfully!
echo Output:
echo   %outputBaseDirPath%
echo.
goto :End

:End
if "%args1%" == "" (
    pause
)
cd /d "%currentPath%"
endlocal
