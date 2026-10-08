@echo off
rem AtlasWH3 web editor: campaign tile map + ground textures (blend TIF) in a browser, e.g. iPad + Apple Pencil.
rem   web_editor.cmd          edit the scratch copies in output\scratch\web_edit (default)
rem   web_editor.cmd kit      edit the 190E kit's real tile_map.png and blend TIF
rem Exposes http://127.0.0.1:5180 to your tailnet with `tailscale serve` (HTTPS); `tailscale serve reset` undoes that.
setlocal
set ROOT=%~dp0..
set KIT=C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS\assembly_kit_190E
set MAP=3k_190e_expanded_map
set EDIT=%ROOT%\output\scratch\web_edit

dotnet build "%ROOT%\src\AtlasWH3.Web" -c Release -v q -nologo || exit /b 1
tailscale serve --bg 5180

if /i "%1"=="kit" (
  "%ROOT%\src\AtlasWH3.Web\bin\Release\net9.0\AtlasWH3.Web.exe" --map %MAP% --ak "%KIT%"
) else (
  for %%f in ("%EDIT%\%MAP%.blend.*.tif") do set BLEND=%%f
  "%ROOT%\src\AtlasWH3.Web\bin\Release\net9.0\AtlasWH3.Web.exe" --map %MAP% --ak "%KIT%" --tilemap "%EDIT%\tile_map.png" --blend "%BLEND%"
)
