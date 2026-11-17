@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET=%ROOT%.tooling\dotnet\dotnet.exe"
set "OUT=%ROOT%Release"
set "MSYS="
set "MSYSTEM="

if not exist "%DOTNET%" (
  echo Fehler: %DOTNET% fehlt.
  exit /b 1
)

"%DOTNET%" publish "%ROOT%WellenEditor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o "%OUT%" || exit /b 1

echo.
echo Build OK: %OUT%\wavebenchpro.exe
copy /Y "%OUT%\wavebenchpro.exe" "%OUT%\WellenEditor.exe" >nul
echo Alias OK: %OUT%\WellenEditor.exe
exit /b 0