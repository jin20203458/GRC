# GRC (GenAI Roleplay Chat)

[![NET Version](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

GRC는 Google Gemini API를 활용하여 캐릭터 롤플레잉 및 소설 창작을 수행하는 C# WPF 기반의 데스크톱 클라이언트입니다.

<img width="1489" height="888" alt="image" src="https://github.com/user-attachments/assets/7a0d602a-32af-4401-aaa8-33e2cda5c0a0" />

<img width="800" alt="GRC Main Loop Demo" src="grc_main.gif" />

---

## 🚀 일반 사용자 무설치 다운로드 (Download)

> **개발 환경(.NET SDK) 설치 없이 바로 실행하고 싶으신가요?**

[![Download Latest Portable Release](https://img.shields.io/badge/Download-Latest_Portable_Release-brightgreen?style=for-the-badge&logo=github)](https://github.com/jin20203458/GRC/releases/latest)

1. 위 다운로드 버튼을 클릭하여 `GRC_v1.0.0_Portable.zip` 파일을 다운로드합니다.
2. 다운로드한 ZIP 파일의 압축을 원하시는 폴더에 풀어줍니다.
3. 압축이 해제된 폴더에서 **`GRC.exe`**를 더블 클릭하여 실행합니다.
4. 초동 실행 안내 및 상세 사용 방법은 압축 파일 내 동봉된 `QUICK_START.txt`를 참조하세요.

> ⚠️ **Windows SmartScreen 경고 안내**
> 개인 오픈소스 소프트웨어 특성상 첫 실행 시 *"Windows가 PC를 보호했습니다"* 경고창이 나타날 수 있습니다. **'추가 정보'** ➔ **'실행'** 버튼을 클릭하시면 정상 실행됩니다. (GRC는 안전한 오픈소스 프로젝트입니다.)

---

## 기술 문서 (Documentation)

프로젝트 전체 아키텍처 및 상세 구현 명세는 `Obsidian.Agent` 저장소에 중앙 집중화되어 관리됩니다. 아래 문서들은 개발자와 코딩 에이전트 모두가 참조하는 단일 진실의 원천(Single Source of Truth)입니다.

- [00_project_overview.md](https://github.com/jin20203458/Obsidian.Agent/blob/main/GRC/docs/00_project_overview.md): GRC 프로젝트 정체성, 비전 및 계층 구조 개요
- [01_grc_architecture.md](https://github.com/jin20203458/Obsidian.Agent/blob/main/GRC/docs/01_grc_architecture.md): 3단계 메모리 파이프라인, O(1) 파일 로거, 비차단 스트리밍 및 동기화 구현 명세
- [02_user_quickstart_guide.md](https://github.com/jin20203458/Obsidian.Agent/blob/main/GRC/docs/02_user_quickstart_guide.md): 초보자/비개발자 포터블 실행, SmartScreen 우회 및 API 키 가이드

---

## 핵심 기능 (Key Features)

- **3단계 메모리 파이프라인**: 단기(최근 18턴) ➔ 중기(10턴 단위 챕터 요약) ➔ 장기(1,500자 단위 연대기 압축)로 구성된 계층적 캐시로 토큰 사용량을 최적화합니다.
- **최신 Gemini 모델 & 추론 레벨 최적화**: 메인 서사와 세션 아키텍트에 `Gemini 3.6 Flash`(High Thinking), 백그라운드 스탯/추천 답글에 `Gemini 3.5 Flash Lite`(Medium Thinking)를 배치하여 반응 속도와 몰입감을 동시에 확보합니다.
- **비차단(Non-blocking) 렌더링**: `System.Threading.Channels` 비동기 스트리밍 및 60FPS 배치 렌더링으로 타자 출력 시 UI 지연을 방지합니다.
- **O(1) 파일 쓰기 최적화**: 전체 JSON 역직렬화 오버헤드 없이 바이너리 탐색(Seek)을 통해 배열 끝부분에 새 로그를 추가하는 고성능 로거를 구현했습니다.
- **AI 세션 아키텍트**: 한 줄의 키워드로 세계관 설정, 인물 사전(로어북), 스탯창 및 프롬프트를 자율 기획하고 감사관 모델을 통해 자가 검수(Self-Review)합니다.
- **멀티모달 감정 TTS**: 텍스트 스트리밍 중 대사를 감지하여 음성을 선행 생성(Prefetch)하고 자막 렌더링 싱크에 맞춰 재생을 제어합니다.

<img width="800" alt="Session Architect Demo" src="session_architect.gif" />

---

## 개발자용 시작 가이드 (Developer Guide)

### 요구 사항 (Prerequisites)
* Windows OS
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 이상

### 빌드 및 실행 (Build & Run)
프로젝트 루트 디렉토리에서 아래 명령을 실행합니다.

```bash
# 의존성 복구 및 빌드
dotnet build

# 애플리케이션 실행
dotnet run --project GRC/GRC.csproj

# 포터블 배포판 자동 빌드 및 ZIP 패키징
powershell -ExecutionPolicy Bypass -File .\build_release.ps1
```

---

## 설정 가이드 (Configuration)

GRC는 Google AI Studio와 Google Cloud Vertex AI 연동을 지원합니다.

### 방법 1. Google AI Studio API 키 사용
* **제약 사항**: Google AI Studio API 키 단독 사용 시, 외부 연동 제약으로 인해 성우 멀티모달 TTS(오디오 대사 낭독) 기능은 활성화되지 않습니다. 해당 기능 활성화를 위해서는 아래의 Google Cloud Vertex AI 연동이 필요합니다.

1. 앱 실행 후 설정(Settings) 메뉴로 이동합니다.
2. 발급받은 API Key를 입력창에 등록하고 저장합니다.
3. 또는 프로젝트 빌드 출력 디렉토리의 `AppSettings.json` 파일에 직접 기술할 수도 있습니다:
   ```json
   {
     "ApiKey": "YOUR_GEMINI_API_KEY",
     "UseVertexAI": false
   }
   ```

### 방법 2. Google Cloud Vertex AI 사용
1. Google Cloud 콘솔에서 Vertex AI API가 활성화된 프로젝트의 서비스 계정 키 파일(`.json`)을 다운로드합니다.
2. 앱 실행 후 설정(⚙) 메뉴에서 **[서비스 계정 키 파일 선택]** 버튼을 통해 다운로드한 키 파일을 선택하여 등록합니다. (또는 `Config/google-credentials.json`에 직접 배치)
3. 구글 클라우드 **Project ID**를 입력합니다.
4. **주의**: **Location(리전) 필드는 반드시 비워두셔야(Empty/Global) 합니다.** Gemini 3.6 Flash 모델은 글로벌 전용 엔드포인트에서 작동합니다.
5. `AppSettings.json` 직접 편집 시 예시:
   ```json
   {
     "ProjectId": "YOUR_PROJECT_ID",
     "Location": "",
     "UseVertexAI": true
   }
   ```

---

## 기여 방법 (Contributing)

기여 프로세스는 아래와 같습니다:

1. 프로젝트 리포지토리를 Fork합니다.
2. 새로운 기능 브랜치를 생성합니다 (`git checkout -b feature/AmazingFeature`).
3. 변경 사항을 Commit합니다 (`git commit -m 'Add some AmazingFeature'`).
4. 브랜치에 Push합니다 (`git push origin feature/AmazingFeature`).
5. Pull Request를 생성하여 검토를 요청합니다.

---

## 라이선스 (License)

본 프로젝트는 MIT License 하에 배포됩니다. 상세 내용은 `LICENSE` 파일을 참조하십시오.
