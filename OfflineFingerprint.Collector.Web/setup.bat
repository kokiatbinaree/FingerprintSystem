@echo off
setlocal
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js 22+ is required.& exit /b 1)
node -e "const v=process.versions.node.split('.').map(Number);if(v[0]<22){process.exit(1)}" || (echo Node.js 22+ is required.& exit /b 1)
npm install
if errorlevel 1 exit /b 1
npm run build
if errorlevel 1 exit /b 1
echo Web setup complete.
