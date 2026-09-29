@echo off
rem Builds the device renderers for the Windows app with MSVC (x64):
rem   tdui_rp2040.dll   from src\          (RP2040-Touch-LCD-1.69, 240x280)
rem   tdui_esp32c3.dll  from esp32c3\src\  (ESP32-2424S012C, 240x240 round)
rem Usage: hostui\build.bat OUTDIR
rem C4244/C4305 (int/double to float) are off: the firmware passes small pixel values as floats.
rem Finds Visual Studio's C++ tools with vswhere, unless already in a developer prompt.
setlocal
set "HOSTUI=%~dp0"
set "ROOT=%~dp0.."
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%~dp0out"
if not exist "%OUT%" mkdir "%OUT%"

if defined VCToolsInstallDir goto build
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto novs
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do set "VCVARS=%%i"
if not defined VCVARS goto novs
call "%VCVARS%" >nul 2>&1
if errorlevel 1 exit /b 1

:build
call :board rp2040 "%ROOT%\src" ""
if errorlevel 1 exit /b 1
call :board esp32c3 "%ROOT%\esp32c3\src" "/DTDUI_FB_PTR"
if errorlevel 1 exit /b 1
exit /b 0

:board
set "SRC=%~2"
set "OBJ=%OUT%\obj_%1"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /LD /O2 /W3 /WX /wd4244 /wd4305 /fp:precise /D_CRT_SECURE_NO_WARNINGS %~3 /I"%SRC%" /I"%HOSTUI%." ^
   "%HOSTUI%tdui.c" "%SRC%\ui_pages.c" "%SRC%\ui_sync.c" "%SRC%\gfx.c" "%SRC%\icons.c" ^
   "%SRC%\jig_lane.c" "%SRC%\jig_paths.c" "%SRC%\aa_fonts.c" ^
   /Fo"%OBJ%\\" /Fe"%OUT%\tdui_%1.dll" /link /NOLOGO >"%OBJ%\build.log"
if errorlevel 1 (
    type "%OBJ%\build.log"
    echo hostui: building tdui_%1.dll failed 1>&2
    exit /b 1
)
exit /b 0

:novs
echo hostui: the Visual Studio C++ tools (MSVC x64) were not found; install the "Desktop development with C++" workload. 1>&2
exit /b 1
