# GRC (GenAI Roleplay Chat) Release Build Automation Script
# usage: .\build_release.ps1 -Version "1.0.0"

param (
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$rootPath = $PSScriptRoot
$projectPath = Join-Path $rootPath "GRC\GRC.csproj"
$distDir = Join-Path $rootPath "dist"
$folderName = "GRC_v${Version}_Portable"
$targetDir = Join-Path $distDir $folderName
$zipFile = Join-Path $distDir "${folderName}.zip"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " GRC Release Portable Build Automation (v$Version)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 기존 빌드 결과물 정리
if (Test-Path $targetDir) {
    Write-Host "[1/5] Removing existing build folder: $targetDir" -ForegroundColor Yellow
    Remove-Item -Path $targetDir -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path $zipFile) {
    Write-Host "[1/5] Removing existing zip file: $zipFile" -ForegroundColor Yellow
    try {
        Remove-Item -Path $zipFile -Force -ErrorAction Stop
    } catch {
        Start-Sleep -Milliseconds 500
        Remove-Item -Path $zipFile -Force -ErrorAction SilentlyContinue
    }
}

# 2. dotnet publish (Self-Contained Release win-x64)
Write-Host "[2/5] Building & Publishing WPF Self-Contained Binary (win-x64)..." -ForegroundColor Green
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $targetDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 3. 기본 디렉터리 구조 생성 (Sessions, Config) 및 보안 클리닝
Write-Host "[3/5] Creating clean directory structure & security sanitization..." -ForegroundColor Green
$configDir = Join-Path $targetDir "Config"
$sessionsDir = Join-Path $targetDir "Sessions"

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
if (-not (Test-Path $sessionsDir)) { New-Item -ItemType Directory -Path $sessionsDir | Out-Null }

# 🔒 개인 인증키(google-credentials.json), API Key가 포함될 수 있는 AppSettings.json 및 개인 세션 파일 완전 삭제
Remove-Item -Path "$configDir\google-credentials.json" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$targetDir\AppSettings.json" -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $sessionsDir -Recurse | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 4. QUICK_START.txt 비개발자용 시작 가이드 생성 및 동봉
Write-Host "[4/5] Creating QUICK_START.txt user guide..." -ForegroundColor Green
$quickStartContent = @"
====================================================================
  GRC (GenAI Roleplay Chat) v${Version} 빠른 시작 가이드 (Quick Start)
====================================================================

■ 1단계: 압축 해제
  - 반드시 이 ZIP 파일의 압축을 원하시는 폴더에 먼저 해제해주세요.
  - 압축을 해제하지 않고 ZIP 내부에서 직접 GRC.exe를 실행하면 오류가 발생합니다.

■ 2단계: 실행하기
  - 압축이 해제된 폴더 내의 GRC.exe 파일을 더블 클릭하여 실행합니다.
  - ⚠ "Windows가 PC를 보호했습니다" 경고창이 나타날 경우:
    1) 경고창 내부의 '추가 정보' 글자를 클릭합니다.
    2) 하단에 새로 나타나는 '실행' 버튼을 클릭하면 정상 실행됩니다.
    * 이 경고는 코드 서명(Code Signing) 인증서가 적용되지 않은 개별 소프트웨어에서
      Windows가 기본 표시하는 문구이며, GRC는 안전한 오픈소스 프로그램입니다.

■ 3단계: API 키 등록 (초기 1회)
  - 앱 실행 후 오른쪽 상단의 ⚙ (설정) 버튼을 누릅니다.
  - Google AI Studio (https://aistudio.google.com/apikey)에서 무료 API Key를 발급받습니다.
  - 발급받은 API 키를 입력창에 붙여넣고 [저장]을 클릭합니다.
  - (Vertex AI 사용자의 경우) 설정 창의 [인증 파일 선택] 버튼으로 google-credentials.json을 등록하며, Location(리전) 필드는 비워두셔야 합니다 (Gemini 3.6 global 지원).

■ 4단계: 세션 생성 및 대화
  - 메인 화면의 ➕ 버튼을 클릭합니다.
  - "사이버펑크 세계관의 하수구 생존기" 처럼 원하시는 주제를 한 줄 입력하면
    AI 세션 아키텍트가 캐릭터, 스탯, 로어북, 시나리오를 알아서 자동 생성합니다!

====================================================================
  공식 저장소: https://github.com/jin20203458/GRC
====================================================================
"@

$quickStartPath = Join-Path $targetDir "QUICK_START.txt"
$utf8WithBom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText($quickStartPath, $quickStartContent, $utf8WithBom)

# 5. ZIP 압축 파일 생성
Write-Host "[5/5] Compressing portable distribution package into ZIP..." -ForegroundColor Green
if (Test-Path $zipFile) {
    try { Remove-Item -Path $zipFile -Force -ErrorAction SilentlyContinue } catch {}
}
Compress-Archive -Path "$targetDir\*" -DestinationPath $zipFile -Force

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Build Successfully Completed!" -ForegroundColor Green
Write-Host " Distribution Folder: $targetDir" -ForegroundColor White
Write-Host " Distribution ZIP File: $zipFile" -ForegroundColor White
Write-Host "==========================================" -ForegroundColor Cyan
