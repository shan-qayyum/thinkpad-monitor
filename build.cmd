@echo off
rem Builds ThinkPadMonitor.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or SDK needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (echo .NET Framework 4 compiler not found at %CSC% & exit /b 1)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0bin\ThinkPadMonitor.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\*.cs" || exit /b 1
copy /y "%~dp0third_party\inpout\inpoutx64.dll" "%~dp0bin\" >nul
copy /y "%~dp0third_party\inpout\InstallDriver.exe" "%~dp0bin\" >nul
copy /y "%~dp0third_party\inpout\inpout32.dll" "%~dp0bin\" >nul
echo Built %~dp0bin\ThinkPadMonitor.exe
