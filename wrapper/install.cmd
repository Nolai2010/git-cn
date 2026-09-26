@echo off
rem 双击安装 git-cn（复制文件 + 加入用户 PATH，原 PATH 会备份）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
