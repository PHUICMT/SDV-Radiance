@echo off
rem Double-click to run the labeler sweep. Resumes by default: profiles already merged are
rem skipped, so an interrupted run costs the pass that was in flight and nothing else.
rem
rem   run-sweep.bat                 resume
rem   run-sweep.bat --restart       start over
rem   run-sweep.bat MapPass-01      just that profile
rem
rem Deliberately NOT a .ps1. PowerShell 5.1 killed two sweeps in one evening: it turns a native
rem exe's stderr into a terminating error, and it reads script files as ANSI so one non-ASCII
rem character stops the file parsing at all. Both failures looked identical from outside.
setlocal
set PY=D:\Program\anaconda3\envs\ml\python.exe
if not exist "%PY%" set PY=python
"%PY%" -u "%~dp0run-sweep.local.py" %*
echo.
pause
