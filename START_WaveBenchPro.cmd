@echo off
setlocal
set "EXE=%~dp0bin\Release\net9.0-windows\win-x64\publish\WaveBenchPro.exe"
if not exist "%EXE%" (
  echo FEHLER: WaveBenchPro.exe nicht gefunden:
  echo   %EXE%
  echo Bitte zuerst mit .tooling\dotnet\dotnet.exe publish ausfuehren.
  pause
  exit /b 1
)
start "" "%EXE%"
endlocal
