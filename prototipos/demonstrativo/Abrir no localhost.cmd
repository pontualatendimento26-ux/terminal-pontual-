@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0servir.ps1"
if errorlevel 1 pause
