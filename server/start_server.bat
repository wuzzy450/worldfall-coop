@echo off
title Worldfall Co-op server  (TCP port 25598)
cd /d "%~dp0cuberite"
echo.
echo  Worldfall Co-op relay - players connect to this PC on TCP port 25598
echo  Type  wf    for players and worlds
echo  Type  stop  to shut the server down (worlds are saved in cuberite\worldfall_rooms)
echo.
"%~dp0cuberite\Cuberite.exe"
echo.
echo Server stopped.
pause
