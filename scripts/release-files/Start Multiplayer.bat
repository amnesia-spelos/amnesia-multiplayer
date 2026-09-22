@echo off
rem Starts Amnesia and then the Game Peer, from wherever this file sits in the Amnesia folder.
cd /d "%~dp0"

if not exist "Amnesia.exe" goto :missing_game
if not exist "multiplayer\Multimnesia.Client.exe" goto :missing_peer

start "" "%~dp0Amnesia.exe"
rem The Game Peer runs from its own folder, so an appsettings.json put beside it is the one read.
rem It retries until the game is listening, so starting it right away is fine.
start "Amnesia Multiplayer" /d "%~dp0multiplayer" "%~dp0multiplayer\Multimnesia.Client.exe"
goto :eof

:missing_game
echo Amnesia.exe was not found next to this file.
echo Extract the archive into your Amnesia folder, then run it from there.
pause
goto :eof

:missing_peer
echo multiplayer\Multimnesia.Client.exe was not found next to this file.
echo Extract the whole archive, keeping its folders, into your Amnesia folder.
pause
