@echo off
chcp 936 >nul
rem ==========================================================
rem  KK Manager by Rushiera - 清空所有数据（回到初始状态）
rem  删除 data 目录下的全部数据库文件：卡片 / mod / 扫描结果 /
rem  库根 / 选项，连 -wal / -shm 附属文件一并删除。
rem  本文件是 ANSI/GBK 编码、CRLF 换行、无 BOM——改文案请保持同一形态。
rem ==========================================================
setlocal
cd /d "%~dp0"
set PORT=8539
set DATA=%~dp0data
set LIST=%TEMP%\kkc_clean_list.txt

echo.
echo  ============================================================
echo   KK Manager by Rushiera —— 清空所有数据（不可撤销）
echo  ============================================================
echo   目标目录 : %DATA%
echo   效果     : 删除该目录下的全部数据库文件
echo              卡片 / mod / 扫描结果 / 库根 / 选项 一并回到初始状态
echo              下次启动会重建一个空库，库根需要重新添加
echo.

if not exist "%DATA%" goto :nodata

(for %%f in ("%DATA%\*.db" "%DATA%\*.db-wal" "%DATA%\*.db-shm") do @echo %%~nxf) >"%LIST%"
set COUNT=0
for /f "usebackq delims=" %%f in ("%LIST%") do set /a COUNT+=1
if %COUNT%==0 goto :nothing

echo   将要删除的文件（共 %COUNT% 个）：
echo.
type "%LIST%"
echo.
echo   说明：面板记住的选中文件夹 / 折叠状态存在浏览器里，
echo         不在数据库中，本工具不会清除。
echo   说明：不做备份，删掉就恢复不了。
echo.

echo  【第一步 / 共两步】确认要清空
set /p ANS1=   继续吗？[y/N] 
if /i not "%ANS1%"=="y" goto :abort

echo.
echo  【第二步 / 共两步】请输入 CLEAN（全大写）以删除上面列出的文件
set /p ANS2=   确认： 
if not "%ANS2%"=="CLEAN" goto :abort
echo.

netstat -ano | findstr ":%PORT% " | findstr "LISTENING" >nul 2>nul
if errorlevel 1 goto :dodelete
echo   面板正运行在端口 %PORT% 上，它占着这些文件。
set /p ANS3=   现在关掉它（按端口结束进程）再继续？[y/N] 
if /i not "%ANS3%"=="y" goto :abort
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":%PORT% " ^| findstr "LISTENING"') do taskkill /PID %%p /F
timeout /t 1 /nobreak >nul

:dodelete
for %%f in ("%DATA%\*.db" "%DATA%\*.db-wal" "%DATA%\*.db-shm") do del /f /q "%%~ff" >nul 2>nul
set LEFT=0
for %%f in ("%DATA%\*.db" "%DATA%\*.db-wal" "%DATA%\*.db-shm") do set /a LEFT+=1
if not "%LEFT%"=="0" goto :leftover

echo  [完成] 已删除 %COUNT% 个文件——数据库已清空。
echo         下次启动 KK Manager by Rushiera 会重建空库。
echo         请在面板里重新添加库根，或用命令行：roots-add。
echo.
del /f /q "%LIST%" >nul 2>nul
pause
exit /b 0

:nodata
echo  [跳过] 这里没有 data 目录——无需清理。
del /f /q "%LIST%" >nul 2>nul
pause
exit /b 0

:nothing
echo  [跳过] 目录里没有数据库文件——无需清理。
del /f /q "%LIST%" >nul 2>nul
pause
exit /b 0

:leftover
echo  [警告] 还有 %LEFT% 个文件没删掉——数据库并未完全清空。
echo         请先关闭面板和所有 KK Manager by Rushiera 进程，然后重跑本工具。
del /f /q "%LIST%" >nul 2>nul
pause
exit /b 3

:abort
echo.
echo  [已取消] 没有删除任何文件。
del /f /q "%LIST%" >nul 2>nul
pause
exit /b 1
