@echo off
setlocal
cd /d "%~dp0"

set "CSC=csc"
where csc >nul 2>&1
if errorlevel 1 (
    set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist "%CSC%" (
    where csc >nul 2>&1
    if errorlevel 1 (
        echo.
        echo Could not find the built-in .NET Framework C# compiler.
        echo Install/enable .NET Framework 4.x, then run this file again.
        echo.
        if not defined CI pause
        exit /b 1
    )
)

echo Building Mac Traffic Lights v4.0...
"%CSC%" /nologo /target:winexe /optimize+ /out:MacTrafficLights.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll MacTrafficLights.cs

if errorlevel 1 (
    echo.
    echo Build failed. Take a screenshot of the errors above and send it to me.
    echo.
    if not defined CI pause
    exit /b 1
)

echo Done. Starting Mac Traffic Lights v4.0...
start "" "%~dp0MacTrafficLights.exe"
exit /b 0

