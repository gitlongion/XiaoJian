@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo Could not find the .NET Framework C# compiler.
  exit /b 1
)

if not exist "dist" mkdir "dist"

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
  /win32manifest:src\XiaoJian\app.manifest ^
  /win32icon:src\XiaoJian\card.ico ^
  /out:dist\XiaoJian.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  src\XiaoJian\Program.cs ^
  src\XiaoJian\Storage.cs ^
  src\XiaoJian\Forms.cs ^
  src\XiaoJian\AssemblyInfo.cs

if errorlevel 1 exit /b %errorlevel%
echo Built: dist\XiaoJian.exe

