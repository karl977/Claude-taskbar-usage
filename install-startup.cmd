@echo off
rem Registers ClaudeTray to start at login. It then appears in Settings > Apps > Startup.
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v ClaudeTray /t REG_SZ /d "\"%~dp0bin\ClaudeTray.exe\"" /f
start "" "%~dp0bin\ClaudeTray.exe"
