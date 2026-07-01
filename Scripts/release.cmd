@echo off
REM Quick release script wrapper
REM Usage: release.cmd [options]
REM Options are passed through to release.ps1

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
