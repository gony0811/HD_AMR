namespace HD_AMR.Data.Entities;

/// <summary>
/// Teaching 페이지에서 관리하는 고정 슬롯형 기준 자세 한 개(예: 홈 위치, 검사 준비 위치).
/// 좌표는 코봇 BASE 좌표계 기준(X,Y,Z mm / Rx,Ry,Rz deg). 캡처 시점의 관절각(J1~J6)도 함께 보관한다.
/// 이동은 BASE 자세로 MoveL(직선) 수행. 슬롯은 미리 생성되며 티칭 전에는 좌표가 비어 있다(null).
/// </summary>
public class TeachingPosition
{
    public int Id { get; set; }

    /// <summary>슬롯 식별자(home, inspectionReady …). 코드 시드와 매칭되는 unique 키.</summary>
    public string Key { get; set; } = "";

    /// <summary>표시 이름(홈 위치, 검사 준비 위치 …).</summary>
    public string Name { get; set; } = "";

    /// <summary>테이블 표시 순서.</summary>
    public int SortOrder { get; set; }

    // BASE 좌표 (mm / deg) — 티칭 전에는 null
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double? Rx { get; set; }
    public double? Ry { get; set; }
    public double? Rz { get; set; }

    // 캡처 시점 관절각 (deg) — 표시/진단용
    public double? J1 { get; set; }
    public double? J2 { get; set; }
    public double? J3 { get; set; }
    public double? J4 { get; set; }
    public double? J5 { get; set; }
    public double? J6 { get; set; }

    /// <summary>캡처 시 활성 tool id(BASE 자세를 읽고 이동할 때 동일 tool을 사용해야 일관성 유지).</summary>
    public int Tool { get; set; }

    /// <summary>마지막 티칭(현재 위치 저장) 시각. 미티칭이면 null.</summary>
    public DateTime? CapturedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>좌표가 캡처되어 이동 가능한 상태인지 여부.</summary>
    public bool IsTaught => X.HasValue;
}
