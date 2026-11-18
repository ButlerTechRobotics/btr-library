@echo off
REM Unregisters the BTR Library add-in. Right-click > Run as administrator. Close SOLIDWORKS first.
setlocal
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
set DLL=%~dp0..\src\bin\Release\net48\BtrLibrary.dll
if not exist "%DLL%" set DLL=%~dp0..\src\bin\Debug\net48\BtrLibrary.dll
"%REGASM%" /u "%DLL%"
pause
