@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%FW%\csc.exe" /nologo /optimize+ /target:winexe /out:"%~dp0bin\ClaudeTray.exe" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:"%WPF%\WindowsBase.dll" ^
  "%~dp0src\ClaudeTray.cs"
endlocal
