@echo off
REM Registers the BTR Library add-in with SOLIDWORKS. Right-click > Run as administrator.
setlocal
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
set DLL=%~dp0..\src\bin\Release\net48\BtrLibrary.dll
if not exist "%DLL%" set DLL=%~dp0..\src\bin\Debug\net48\BtrLibrary.dll
if not exist "%DLL%" (
  echo Could not find BtrLibrary.dll. Build the project in Visual Studio first.
  pause
  exit /b 1
)
"%REGASM%" /codebase "%DLL%"
if errorlevel 1 (
  echo.
  echo Registration failed. Make sure you ran this as administrator.
) else (
  echo.
  echo Done. Restart SOLIDWORKS. The BTR Library tab appears in the Task Pane on the right.
)
pause
