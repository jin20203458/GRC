using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GRC.Models;

/// <summary>
/// 에이전트의 전체 작업 계획. 유저 컨셉으로부터 AI가 생성한 서사 설계 청사진(Blueprint).
/// </summary>
public class AgentPlan
{
    [JsonIgnore]
    public string Concept { get; set; } = "";                      // 유저 원본 컨셉 입력

    [JsonPropertyName("sessionTitle")]
    public string SessionTitle { get; set; } = "";                 // 세션 고유 제목

    [JsonPropertyName("surfaceTone")]
    public string SurfaceTone { get; set; } = "";                  // 일상/표면 호흡 (대화 템포, 평소 분위기)

    [JsonPropertyName("coreWeight")]
    public string CoreWeight { get; set; } = "";                   // 세계관/갈등의 무게감 및 핵심 서사 동력

    [JsonPropertyName("pcInitialPosition")]
    public string PcInitialPosition { get; set; } = "";            // 주인공(PC)의 초기 위상 및 직면 과제

    [JsonPropertyName("worldviewOutline")]
    public string WorldviewOutline { get; set; } = "";             // 세계관 핵심 아웃라인

    [JsonPropertyName("lorebookPlan")]
    public List<LorebookPlanItem> LorebookPlan { get; set; } = []; // 로어북 항목 정밀 기획 리스트

    [JsonPropertyName("statsPlan")]
    public List<string> StatsPlan { get; set; } = [];              // 스탯 이름 계획 리스트

    [JsonPropertyName("scenarioOutline")]
    public string ScenarioOutline { get; set; } = "";              // 초기 시작 시나리오 및 첫 직면 상황 설계

    [JsonPropertyName("promptOutline")]
    public string PromptOutline { get; set; } = "";                // GM 서술 스타일, 문체 톤앤매너 디렉팅
}

public class LorebookPlanItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";      // 인물, 장소, 아이템, 세계관

    [JsonPropertyName("brief")]
    public string Brief { get; set; } = "";         // 핵심 요약 설명

    [JsonPropertyName("surfaceRole")]
    public string SurfaceRole { get; set; } = "";   // 외양, 표면적 위상 및 일상 태도

    [JsonPropertyName("innerDepth")]
    public string InnerDepth { get; set; } = "";    // 내면의 입체성, 동기, 역린 또는 서사적 훅

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = []; // 검색 트리거용 순수 명사 키워드

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;          // 검색 우선순위 (낮을수록 최우선: 0=핵심, 1=주요, 2=일반, 3=배경)
}

/// <summary>
/// 상태창 및 Turn 0 초기 컨텍스트 시딩 설계 결과 모델
/// </summary>
public class StatusDesignPayload
{
    [JsonPropertyName("stats")]
    public Dictionary<string, string> Stats { get; set; } = new();

    [JsonPropertyName("guide")]
    public string Guide { get; set; } = "";

    [JsonPropertyName("initialChars")]
    public Dictionary<string, string> InitialChars { get; set; } = new();

    [JsonPropertyName("initialPlaces")]
    public List<string> InitialPlaces { get; set; } = [];

    [JsonPropertyName("initialItems")]
    public List<string> InitialItems { get; set; } = [];
}

/// <summary>
/// 에이전트 워크플로우의 각 단계를 추적합니다.
/// </summary>
public enum AgentStep
{
    Idle,              // 대기 (컨셉 입력 전)
    Planning,          // 전체 계획 수립 중
    PlanReview,        // 계획 검토 대기
    WorldviewGen,      // 세계관 생성 중
    WorldviewReview,   // 세계관 검토 대기
    LorebookGen,       // 로어북 생성 중
    LorebookReview,    // 로어북 검토 대기
    StatusGen,         // 상태창 설계 중
    StatusReview,      // 상태창 검토 대기
    ScenarioGen,       // 시나리오 생성 중
    ScenarioReview,    // 시나리오 검토 대기
    PromptGen,         // 시스템 프롬프트 생성 중
    PromptReview,      // 시스템 프롬프트 검토 대기
    Applying,          // 세션 파일 생성/적용 중
    Complete           // 완료
}

/// <summary>
/// 에이전트 채팅창의 메시지. 일반 텍스트 + 구조화된 블록을 모두 지원합니다.
/// </summary>
public class ArchitectMessage
{
    public string Role { get; set; } = "assistant";    // "user" | "assistant" | "system"
    public string Text { get; set; } = "";             // 일반 텍스트
    public AgentStep? RelatedStep { get; set; }        // 이 메시지가 관련된 단계
    public bool HasActionButtons { get; set; }         // 승인/수정 버튼 표시 여부
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// 에이전트의 현재 작업 상태를 나타내는 전체 컨텍스트.
/// </summary>
public class ArchitectSession
{
    public AgentStep CurrentStep { get; set; } = AgentStep.Idle;
    public AgentPlan? Plan { get; set; }
    
    // 생성된 결과물 (단계별 저장)
    public string? GeneratedWorldview { get; set; }
    public List<LorebookEntry>? GeneratedLorebooks { get; set; }
    public Dictionary<string, string>? GeneratedStats { get; set; }
    public string? GeneratedStatusGuide { get; set; }
    public Dictionary<string, string>? GeneratedInitialChars { get; set; }
    public List<string>? GeneratedInitialPlaces { get; set; }
    public List<string>? GeneratedInitialItems { get; set; }
    public string? GeneratedScenario { get; set; }
    public string? GeneratedSystemPrompt { get; set; }
    
    // 대화 이력 (AI 컨텍스트 유지용)
    public List<ArchitectMessage> Messages { get; set; } = [];
    
    // 기존 세션 수정 모드일 경우
    public string? ExistingSessionFileName { get; set; }
    public CharacterPreset? ExistingPreset { get; set; }
}
