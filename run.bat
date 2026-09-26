@echo off
chcp 65001 > nul
title IRIS PxS
cd /d "%~dp0"
echo IRIS PxS を起動しています...
dotnet run -c Release --no-restore
