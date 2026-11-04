@echo off
chcp 65001 >nul
title 数字花园一键部署器
cd /d "%~dp0"

if not exist "deployer.ps1" (
  echo [错误] 缺少 deployer.ps1，请把整个 deployer 文件夹一起复制过来。
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0deployer.ps1"
if errorlevel 1 (
  echo.
  echo 部署器已退出。如果窗口一闪就没了，请查看 %%TEMP%%\garden-deployer-error.log
  pause
)
