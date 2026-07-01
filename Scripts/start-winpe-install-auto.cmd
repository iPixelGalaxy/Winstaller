@echo off
REM WinPE Winstaller Launcher (Automated/No Pause)

timeout /t 3 /nobreak
wpeutil initializenetwork

REM Find and copy Winstaller to X:\
for %%d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%d:\Winstaller\Winstaller.exe" (
        mkdir X:\Winstaller 2>nul
        xcopy /E /Y /Q "%%d:\Winstaller\*" "X:\Winstaller\" >nul
        goto :run
    )
)
echo Winstaller not found!
exit /b 1

:run
X:\Winstaller\Winstaller.exe --debug --update
X:\Winstaller\Winstaller.exe --debug --winpe
