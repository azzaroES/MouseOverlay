@echo off
rem Builds MouseOverlay.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ ^
  /win32manifest:app.manifest /out:MouseOverlay.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll MouseOverlay.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built MouseOverlay.exe
