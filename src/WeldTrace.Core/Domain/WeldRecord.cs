namespace WeldTrace.Core.Domain;

/// <summary>
/// 레이저 용접기 1회 용접 기록. 원본 CSV 한 행에 해당한다.
/// </summary>
/// <param name="PageNo">용접 프로그램 스텝 번호 (1~39). 모듈 하나는 39스텝으로 구성된다.</param>
/// <param name="Speed">용접 속도 설정값</param>
/// <param name="Length">용접 길이 설정값 (mm)</param>
/// <param name="RealPower">실측 출력 (W)</param>
/// <param name="SetFrequency">주파수 설정값</param>
/// <param name="SetDuty">듀티 설정값 (%)</param>
/// <param name="SetPower">출력 설정값 (%)</param>
/// <param name="GateOnTime">게이트 온 시간 (ms)</param>
/// <param name="WorkingTime">용접 시각</param>
public sealed record WeldRecord(
    int PageNo,
    int Speed,
    decimal Length,
    int RealPower,
    int SetFrequency,
    int SetDuty,
    int SetPower,
    int GateOnTime,
    DateTime WorkingTime);
