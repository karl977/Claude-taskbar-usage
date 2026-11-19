@echo off
rem Removes ClaudeTray from login startup and stops it.
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v ClaudeTray /f
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" /v ClaudeTray /f 2>nul
taskkill /im ClaudeTray.exe /f 2>nul
