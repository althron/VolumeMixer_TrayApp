@echo off
setlocal

echo ============================
echo   Building VolMixer-Tray
echo ============================
echo.

REM Change to the directory where this script is located
cd /d "%~dp0"

REM Path to the .NET Framework CSC compiler (64-bit)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo ERROR: C# compiler not found at:
    echo %CSC%
    echo.
    pause
    exit /b 1
)

REM Compile the EXE
"%CSC%" ^
  /nologo /target:winexe /optimize+ ^
  /win32icon:"batch_g2_VolMixer.ico" ^
  /resource:"volmixer.ico",VolMixerTray.Icons.Dark.ico ^
  /resource:"volmixer_black.ico",VolMixerTray.Icons.Light.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "Vol-Mixer-Tray.cs"

if errorlevel 1 (
    echo.
    echo Build failed.
    echo.
    pause
    exit /b 1
)

echo.
echo Build completed successfully!
echo Output: Vol-Mixer-Tray.exe
echo.
pause
