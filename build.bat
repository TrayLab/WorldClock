@echo off
rem Builds WorldClock.exe with the C# compiler that is already part of Windows (.NET Framework 4).
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find the .NET Framework 4 compiler ^(csc.exe^).
  pause
  exit /b 1
)
taskkill /im WorldClock.exe /f >nul 2>&1
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:WorldClock.ico /out:WorldClock.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll *.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built WorldClock.exe - starting it.
start "" "%~dp0WorldClock.exe"
