using System.Collections.Generic;

namespace GRC.Models;

/// <summary>
/// 특정 키워드나 상황에 반응하여 AI에게 주입될 로어북(동적 배경지식) 데이터 구조입니다.
/// </summary>
public record LorebookEntry(
    string Name,                  // 로어북 항목 이름 (예: "칼리고 안개", "일리아")
    List<string> Keywords,        // 스캔 시 반응할 트리거 키워드들 (예: ["칼리고", "안개", "검은 연기"])
    string Content,               // 프롬프트에 주입될 실제 설정 내용
    string Category,            // 카테고리 (예: "인물", "장소", "아이템", "세계관") - 상태창 연동 시 활용
    int Priority = 0
);