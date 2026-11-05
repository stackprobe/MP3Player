CALL Clean.bat

Powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Release.ps1" %*

TIMEOUT 2
