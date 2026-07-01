@echo off
REM WinPE Winstaller Launcher
REM Finds Winstaller on install media, copies to X:\, then runs

echo.
echo =====================================================================
echo   WinPE Winstaller Launcher
echo =====================================================================
echo.

REM Wait for system to settle
timeout /t 3 /nobreak

REM Initialize network
echo [INFO] Initializing network...
wpeutil initializenetwork

REM Search for Winstaller on install media
echo [INFO] Searching for Winstaller...
set "SOURCE="
for %%d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%d:\Winstaller\Winstaller.exe" (
        set "SOURCE=%%d:\Winstaller"
        goto :found
    )
)

echo [ERROR] Winstaller not found on any drive!
pause
exit /b 1

:found
echo [INFO] Found: %SOURCE%

REM Copy to X:\ (writable RAM disk) for updates
echo [INFO] Copying to X:\Winstaller...
mkdir X:\Winstaller 2>nul
xcopy /E /Y /Q "%SOURCE%\*" "X:\Winstaller\" >nul
echo [INFO] Copied to X:\Winstaller

REM Check for updates
echo [INFO] Checking for updates...
X:\Winstaller\Winstaller.exe --debug --update

REM Run WinPE installation
echo [INFO] Starting WinPE installation...
X:\Winstaller\Winstaller.exe --debug --winpe

echo.
echo [INFO] Installation complete.
pause
