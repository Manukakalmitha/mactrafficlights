@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo Could not find the built-in .NET Framework C# compiler.
    echo Install/enable .NET Framework 4.x and try again.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:MacTrafficLights.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll MacTrafficLights.cs
if errorlevel 1 (
    echo.
    echo Build failed. Take a screenshot of the errors above and send it to me.
    pause
    exit /b 1
)

echo Built successfully: MacTrafficLights.exe (v4.0)
pause
