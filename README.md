# GRC (GenAI Roleplay Chat)

GRC는 Google Gemini API를 활용하여 고도로 몰입감 있고 생동감 넘치는 캐릭터와 롤플레잉 및 소설 창작을 즐길 수 있는 **C# WPF 기반의 데스크톱 클라이언트**입니다. 

단순히 이전 대화를 나열하여 보내는 한계를 뛰어넘어, 대규모 서사를 효율적으로 유지하기 위한 **3단계 압축 기억 메커니즘**, 지능적인 **재귀적 로어북**, 그리고 성우처럼 감정을 담아 말하는 **Gemini 멀티모달 TTS 워크플로우** 등이 탑재되어 있어 고품질의 AI 롤플레이 환경을 제공합니다.

---

## 🌟 핵심 기능 (Key Features)

### 1. 3단계 메모리 파이프라인 (3-Tier Memory Pipeline)
긴 서사의 흐름을 잃지 않고 AI의 기억 용량 한계를 극복하기 위해 기억을 3단계로 정밀하게 압축하고 관리합니다.
* **단기 기억 (Raw History)**: 최근 주고받은 날것 상태의 대화(최대 18턴)를 원본 그대로 보존하여 즉각적인 맥락 대처가 가능하게 합니다.
* **중기 기억 (Chapter Plot)**: 단기 기억이 포화되면 오래된 8턴의 메시지를 잘라내어 **Gemini Flash** 모델을 통해 현재 챕터의 누적 줄거리(`Plot`)로 자동 요약 및 융합합니다.
* **장기 기억 (Chronicle)**: 챕터 버퍼가 큐(Queue)를 벗어나 밀려날 때, **Gemini Pro** 모델을 사용해 전체 서사 흐름에서 핵심적인 인과율과 세계관 변화(Canon Events)만을 선별하여 최대 1,500자 이내의 대과거 연대기(`Chronicle`)로 녹여냅니다.

### 2. 동적 로어북 시스템 (Dynamic Lorebook)
* **재귀적 매칭 (Recursive Triggering)**: 최근 대화 내역 및 상태창의 인물/장소 키워드를 분석하여 관련된 설정북을 프롬프트에 동적 삽입합니다. 주입된 본문의 키워드가 또 다른 설정을 꼬리 물어 활성화시키는 연쇄적인 루프 감지 기능이 포함되어 있습니다.
* **대과거 기억 추출**: 대화 도중 중요한 사건이 발생했을 때, 사용자가 수동으로 특정 메시지를 AI가 **대과거 시제(~했었다)** 형태의 영구 기억(`.json`)으로 정제하게 하여 로어북에 저장하는 기능을 제공합니다.

### 3. 멀티모달 감정 연기 TTS (Gemini Multimodal TTS)
* **감정선 반영 성우 연기**: 텍스트를 기계적으로 읽어주는 일반적인 TTS 엔진과 달리, `gemini-3.1-flash-tts-preview` 모델의 오디오 모달리티를 사용합니다. 캐릭터의 기본 성향 정보와 현재의 감정 디렉팅(지문 및 독백의 맥락)을 동봉해 전송하므로, **상황에 맞춰 감정이 살아 있는 성우급 오디오 연기**를 재생합니다.
* **일본어 자동 번역 낭독**: 일본어 낭독을 원할 경우 백그라운드에서 **Gemini Flash Lite**가 대사를 자연스러운 구어체 일본어로 즉석 번역한 뒤 일본어 성우 목소리로 낭독하게 합니다.
* **프로듀서-컨슈머 스트리밍**: 대화가 출력되는 동안 대사를 미리 비동기 추출하여 오디오 생성을 요청(Prefetch)하고, 화면의 타이핑이 완료되는 타이밍에 정확히 싱크를 맞춰 재생을 시작합니다.

### 4. 실시간 상태창 연동 & 시스템 개입 (Status & Meta Directives)
* **상태창 자동 파싱**: AI의 응답 데이터 하단에 출력되는 `<status>` JSON 태그를 검출해 인벤토리, 현재 위치, 인물 상태, 사용자 정의 스탯(호감도, 타락도 등)을 UI에 실시간 바인딩합니다.
* **메타 디렉션 (System Interventions)**: 인물 관계 스탯의 수치가 한계치(최대치 또는 0)에 도달했을 때, 혹은 챕터가 전환될 때 시스템이 AI 프롬프트에 강제로 개입하여 극적인 연출이나 개연성 있는 장면 전환을 주도하도록 지시문을 비밀리에 주입합니다.

### 5. 평행세계 분기 (Branching) 및 추천 답변 프리페치
* **평행세계 분기**: 과거 대화 로그 중 특정 시점의 대화 카드를 마우스 우클릭하여 해당 순간을 기준으로 완전히 독립된 별도의 평행세계 세션 파일(`.json`)로 분기할 수 있습니다.
* **추천 선택지**: AI의 대답이 텍스트로 타이핑되는 동안, 플레이어가 취할 수 있는 3종의 다채로운 대사/행동 선택지(수용/반발/제3의 행동)를 백그라운드에서 미리 예측 생성하여 대사가 끝나는 즉시 유저에게 제시합니다.

---

## 📁 프로젝트 폴더 구조 (Architecture)

본 프로젝트는 깔끔한 **MVVM (Model-View-ViewModel)** 패턴과 의존성 주입(DI) 형태로 설계되었습니다.

```bash
c:\GRC\GRC
│  App.xaml
│  App.xaml.cs          # 애플리케이션 진입점, DI 컨테이너(ServiceCollection) 구성
│  AssemblyInfo.cs
│  GRC.csproj
│
├─Config
│      google-credentials.json  # 구글 클라우드 Vertex AI 자격증명 파일 (Git 커밋 제외)
│
├─Helpers               # 유틸리티 및 특화 헬퍼들
│      AutoScrollHelper.cs
│      ChatDataHelper.cs
│      FullHistoryLogger.cs     # 전체 대화 원본 로그 저장 헬퍼
│      LlmJsonParser.cs         # LLM 출력에서 JSON 및 태그 정밀 추출/역직렬화
│      MemoryEventLogger.cs
│      MessageRoleplayConverter.cs
│      SimpleMarkdownHelper.cs
│      StatefulStreamingHelper.cs
│      TokenLogger.cs           # 대화별 토큰 소모량 로깅 헬퍼
│      WindowTitleBarBehavior.cs
│
├─Models                # 데이터 모델 명세
│      AppSettings.cs           # 앱 환경 설정 모델 (API 키, 테마, 오디오 볼륨 등)
│      ChapterContext.cs        # 챕터별 줄거리 및 인물/장소/아이템 상태 명세
│      CharacterPreset.cs       # 캐릭터 프롬프트 및 설정값 프리셋 DTO
│      ChatMessage.cs           # 개별 메시지 (user/model/system) 구조
│      ChatSession.cs           # 세션 파일 복구/저장용 통합 모델
│      GeminiApiDto.cs          # 제미나이 API 전송 및 응답 규격 DTO
│      LorebookEntry.cs         # 로어북 개별 엔트리 구조
│      StatusPayload.cs         # AI로부터 수신하는 상태 데이터 규격
│
├─Properties
│  └─PublishProfiles
│
├─Resources             # 배경 이미지, 효과음 및 폰트 파일
│  ├─Cyberpunk
│  ├─Fantasy
│  └─Modern
│
├─Services              # 핵심 비즈니스 로직 및 통신 서비스
│      AppSettingsService.cs    # 앱 환경설정 파일(.json) 영속화
│      AudioService.cs          # BGM, 타건음 제어 및 재생 완료된 TTS 임시 파일 청소
│      ChatWorkflowService.cs   # 실시간 채팅 스트리밍 큐잉 및 연기 TTS 제어
│      GeminiApiService.cs      # Google Cloud / AI Studio 제미나이 연동
│      GeminiTtsService.cs      # Gemini 오디오 모달리티 연기 TTS API 연동
│      GoogleAuthService.cs     # 싱글톤 기반의 구글 OAuth 액세스 토큰 관리 공용 서비스
│      LorebookService.cs       # 키워드 스캔 및 재귀적 설정북 주입 로직
│      ReplySuggestionService.cs# 추천 선택지 3종 예측 생성 서비스
│      SessionService.cs        # 세션 원자적 파일 쓰기(Atomic Write)를 통한 데이터 보호
│      ThemeService.cs
│
├─Themes
│      ModernStyles.xaml        # 현대적인 커스텀 다크테마 스타일 리소스
│
├─ViewModels            # MVVM의 프레젠테이션 레이어
│      ChatViewModel.cs         # 대화방 화면 컨트롤러 (분기, 삭제, 저장, 전송 등)
│      MainViewModel.cs         # 네비게이션 제어
│      SessionListViewModel.cs  # 저장된 세션 카드 및 신규 생성 다이얼로그
│      SettingsViewModel.cs     # 환경 설정 UI 데이터 바인딩 및 음량/딜레이 보정
│
└─Views                 # MVVM의 뷰 레이어 (XAML 및 코드 비하인드)
        ChatView.xaml
        ChatView.xaml.cs
        CustomMessageBoxWindow.xaml
        EditInitialScenarioWindow.xaml
        EditLorebookWindow.xaml
        EditWorldviewWindow.xaml
        MainWindow.xaml
        NewSessionSetupWindow.xaml
        SessionListView.xaml
        SettingsView.xaml
        StatusWindow.xaml
        StoryHistoryWindow.xaml
```

---

## 🚀 시작 가이드 (Quick Start)

### 1. 요구 사항 (Prerequisites)
* Windows OS
* **.NET 8.0 SDK** 이상 설치 필요
* API 연동 방법 (다음 중 하나 필수):
  * **Google AI Studio API Key**: 가장 빠르고 간단하게 사용 가능
  * **Google Cloud 서비스 계정 키 파일**: Vertex AI 크레딧 사용 및 대용량 트래픽 통신 시 필요

### 2. 구글 클라우드 자격증명 설정 (Vertex AI 사용 시)
1. 구글 클라우드 콘솔에서 Vertex AI API가 활성화된 프로젝트의 **서비스 계정 키 파일(`.json`)**을 생성하여 다운로드합니다.
2. 다운로드한 JSON 파일의 이름을 `google-credentials.json`으로 변경합니다.
3. 해당 파일을 아래 경로에 위치시킵니다:
   `C:\GRC\GRC\Config\google-credentials.json`
   *(주의: 이 경로는 `.gitignore`에 등록되어 있어 깃허브에 커밋되지 않으니 안심하셔도 됩니다.)*

### 3. 빌드 및 실행
명령 프롬프트 또는 PowerShell을 열고 프로젝트 루트 디렉토리(`C:\GRC`)에서 다음 명령을 실행합니다.

```bash
# 의존성 복구 및 빌드
dotnet build

# 애플리케이션 실행
dotnet run --project GRC/GRC.csproj
```

---

## 📦 기술 스택 및 오픈소스 (Dependencies)
* **Framework**: .NET 8.0 (Windows Presentation Foundation)
* **MVVM Toolkit**: `CommunityToolkit.Mvvm` (프레젠테이션 레이어 상태 제어)
* **Google Auth**: `Google.Apis.Auth` (Vertex AI용 OAuth2 토큰 발급)
* **Dependency Injection**: `Microsoft.Extensions.DependencyInjection` (의존성 일원화)
* **Media / Sound**: `System.Windows.Media.MediaPlayer` (효과음, BGM 및 연기 오디오 스트림 재생)
