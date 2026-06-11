using System;
using System.Collections.Generic;

namespace GRC.Models;

/// <summary>
/// 에이전트의 전체 작업 계획. 유저 컨셉으로부터 AI가 생성한 청사진.
/// </summary>
public class AgentPlan
{
    public string Concept { get; set; } = "";           // 유저 원본 컨셉 입력
    public string WorldviewOutline { get; set; } = "";   // 세계관 개요 (1-2줄 요약)
    public List<LorebookPlanItem> LorebookPlan { get; set; } = []; // 로어북 항목 계획
    public List<string> StatsPlan { get; set; } = [];    // 스탯 이름 계획 리스트
    public string ScenarioOutline { get; set; } = "";    // 시나리오 개요 (1-2줄)
    public string PromptOutline { get; set; } = "";      // 시스템 프롬프트 스타일 개요
}

public class LorebookPlanItem
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";  // 인물, 장소, 아이템, 세계관
    public string Brief { get; set; } = "";     // 한 줄 설명
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
    public string? GeneratedScenario { get; set; }
    public string? GeneratedSystemPrompt { get; set; }
    
    // 대화 이력 (AI 컨텍스트 유지용)
    public List<ArchitectMessage> Messages { get; set; } = [];
    
    // 기존 세션 수정 모드일 경우
    public string? ExistingSessionFileName { get; set; }
    public CharacterPreset? ExistingPreset { get; set; }
}
