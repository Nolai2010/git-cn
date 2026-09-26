@echo off
setlocal
set "HERE=%~dp0"

REM Keep this whole file ASCII: cmd.exe reads batch files in the OEM codepage
REM (GBK on Chinese Windows), so UTF-8 Chinese here turns into garbage bytes and
REM breaks parsing of the lines that follow. All user-facing Chinese lives in
REM git_cn.py, which sets the console up properly.
REM
REM "if not errorlevel 1" rather than "if %errorlevel%==0": %errorlevel% inside a
REM parenthesised block is expanded when the block is parsed, which propagates
REM the wrong exit code.

where py >nul 2>nul
if not errorlevel 1 goto try_py

where python >nul 2>nul
if not errorlevel 1 goto try_python

echo [git-cn] Python 3 not found. Install it with:  winget install Python.Python.3
exit /b 127

:try_py
py -3 "%HERE%git_cn.py" %*
exit /b %errorlevel%

:try_python
python "%HERE%git_cn.py" %*
exit /b %errorlevel%
