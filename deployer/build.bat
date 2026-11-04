@echo off
rem ============================================================
rem  Build Garden Deployer into a single standalone .exe
rem  Requires nothing installed: the compiler ships with Windows
rem  (.NET Framework 4.x is built into Windows 10 / 11)
rem
rem  NOTE: keep this file pure ASCII. Changing the console code
rem  page (chcp) inside a .bat makes cmd resume reading at a wrong
rem  byte offset, so any non-ASCII line gets chopped into garbage
rem  commands. All UI text lives in the C# sources instead.
rem
rem  No application manifest is embedded on purpose: a hand written
rem  manifest that the loader rejects makes the exe fail with
rem  "side-by-side configuration is incorrect". High DPI is enabled
rem  from code via SetProcessDPIAware.
rem ============================================================
setlocal
cd /d "%~dp0"

rem --- 1. locate the .NET Framework compiler dir ---
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if exist "%FW%\csc.exe" goto HAVE_FW
set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
:HAVE_FW
if not exist "%FW%\csc.exe" goto NO_CSC

rem --- 2. locate WPF assemblies (they live in the WPF subfolder) ---
set WPFREF=%FW%\WPF
if not exist "%WPFREF%\PresentationFramework.dll" goto NO_WPF

set XAMLREF=%FW%\System.Xaml.dll
if not exist "%XAMLREF%" set XAMLREF=%WPFREF%\System.Xaml.dll

echo Framework : %FW%
echo WPF       : %WPFREF%
echo.

set OUT=GardenDeployer.exe
if exist "%OUT%" del /q "%OUT%"

rem --- 2b. pack the blog template into payload.zip (embedded into the exe) ---
rem  This is what makes the exe self contained: users only download the exe,
rem  no template folder needed next to it.
set ROOT=%~dp0..
if exist "payload.zip" del /q "payload.zip"
echo Packing blog template...
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\pack-payload.ps1" -Root "%ROOT%" -Out "payload.zip"
if errorlevel 1 goto NO_ZIP
if not exist "payload.zip" goto NO_ZIP
echo.

rem --- 3. compile ---
echo Compiling...
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUT%" /win32icon:"src\app.ico" /resource:"src\ui.xaml" /resource:"src\app.ico" /resource:"payload.zip" /r:"%WPFREF%\PresentationFramework.dll" /r:"%WPFREF%\PresentationCore.dll" /r:"%WPFREF%\WindowsBase.dll" /r:"%XAMLREF%" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" "src\Program.cs" "src\Shell.cs" "src\Engine.cs"

if errorlevel 1 goto FAIL

del /q payload.zip 2>nul
if exist "%STAGE%" rd /s /q "%STAGE%" 2>nul

echo Build OK  -^>  %OUT%
for %%F in ("%OUT%") do echo Size: %%~zF bytes
echo.

rem --- 4. run the built-in self test and print the report ---
set REPORT=%TEMP%\garden-selftest.txt
if exist "%REPORT%" del /q "%REPORT%"
echo Running self test...
start "" /wait "%OUT%" --selftest

echo.
if not exist "%REPORT%" goto NO_REPORT

echo ---------- self test report ----------
rem The report is UTF-8; let PowerShell do the transcoding so Chinese shows correctly.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath (Join-Path $env:TEMP 'garden-selftest.txt') -Encoding UTF8"
if errorlevel 1 type "%REPORT%"
echo --------------------------------------
echo.
echo Next: double click %OUT% to run the deployer.
echo Tip: you can rename the exe, e.g. to a Chinese name.
echo.
pause
exit /b 0

:NO_REPORT
echo [ERROR] The exe did not start, no report produced.
echo Run this in a console to see the reason:
echo     %OUT% --selftest
echo.
pause
exit /b 1

rem ============================================================ error exits

:NO_CSC
echo.
echo [ERROR] Compiler not found under %WINDIR%\Microsoft.NET\Framework64\v4.0.30319
echo .NET Framework 4.x seems to be missing on this machine.
echo.
pause
exit /b 1

:NO_PAYLOAD
echo.
echo [ERROR] Blog template not found: %ROOT%\src\build.js
echo Run build.bat from the deployer folder inside the blog repository.
echo.
pause
exit /b 1

:NO_ZIP
echo.
echo [ERROR] Could not create payload.zip
echo Check that PowerShell is available and %TEMP% is writable.
echo.
pause
exit /b 1

:NO_WPF
echo.
echo [ERROR] WPF assemblies not found in:
echo         %WPFREF%
echo This machine lacks the .NET Framework WPF component.
echo.
pause
exit /b 1

:FAIL
echo.
echo [ERROR] Compilation failed. Copy the messages above and send them back.
echo.
pause
exit /b 1
