@echo off
setlocal
set "HERE=%~dp0"
where py >nul 2>nul
if %errorlevel%==0 (
    py -3 "%HERE%git_cn.py" %*
    exit /b %errorlevel%
)
where python >nul 2>nul
if %errorlevel%==0 (
    python "%HERE%git_cn.py" %*
    exit /b %errorlevel%
)
echo [git-cn] 没找到 Python 3。装一个：winget install Python.Python.3
exit /b 127
