namespace WeldTrace.Core.Spc;

public sealed record TrendLine(double Slope, double Intercept, int Count)
{
    public double ValueAt(double x) => Intercept + Slope * x;

    /// <summary>
    /// 현재 추세가 이어진다고 가정할 때, 지금(x = now)부터 값이 threshold 아래로 내려가기까지 남은 x.
    ///   - 추세선 값이 이미 threshold 이하이면 0
    ///   - 추세가 내려가지 않으면(기울기 ≥ 0) 닿지 않으므로 null
    /// </summary>
    public double? RemainingUntilBelow(double threshold, double now)
    {
        if (ValueAt(now) <= threshold) return 0;
        if (Slope >= 0) return null;
        return (threshold - Intercept) / Slope - now;
    }
}

/// <summary>
/// 최소제곱 직선 추세. 출력 비율이 판정 임계값(예: 0.95)에 닿는 시점을 추정해 정비 시점을 제안한다.
/// 추정일 뿐이므로 화면에는 "현재 추세가 유지될 경우"라는 전제를 함께 표시한다.
/// </summary>
public static class TrendForecaster
{
    public static TrendLine Fit(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
            throw new ArgumentException("추세를 구하려면 점이 2개 이상 필요합니다.", nameof(points));
        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var sxx = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        if (sxx == 0)
            throw new ArgumentException("X 값이 모두 같아 기울기를 구할 수 없습니다.", nameof(points));
        var sxy = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var slope = sxy / sxx;
        return new TrendLine(slope, meanY - slope * meanX, points.Count);
    }
}
