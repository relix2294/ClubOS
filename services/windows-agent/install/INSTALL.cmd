@echo off
rem ClubOS Agent: double-click to install. Asks for administrator rights, then runs the setup wizard.
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-agent.ps1"
echo.
pause
