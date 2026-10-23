namespace WeldTrace.Core.Spc;

/// <summary>관리도 기준(Phase I): 정상으로 확인된 기간의 평균과 표준편차.</summary>
public sealed record Baseline(double Mean, double StdDev, int Count)
{
    /// <summary>표본 평균과 표본 표준편차(n-1)로 기준을 만든다.</summary>
    public static Baseline FromSamples(IEnumerable<double> samples)
    {
        var xs = samples.ToList();
        if (xs.Count < 2)
            throw new ArgumentException("기준을 만들려면 표본이 2개 이상 필요합니다.", nameof(samples));
        var mean = xs.Average();
        var variance = xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);
        if (variance <= 0)
            throw new ArgumentException("표본의 분산이 0이라 관리 한계를 정할 수 없습니다.", nameof(samples));
        return new Baseline(mean, Math.Sqrt(variance), xs.Count);
    }
}

public sealed record EwmaPoint(int Index, double Value, double Ewma, double Lcl, double Ucl)
{
    public bool BelowLcl => Ewma < Lcl;
    public bool AboveUcl => Ewma > Ucl;
    public bool OutOfControl => BelowLcl || AboveUcl;
}

/// <summary>
/// EWMA 관리도. 작은 평균 이동(서서히 진행되는 출력 열화)에 민감하다.
///   z_i = λ·x_i + (1-λ)·z_{i-1},  z_0 = μ0
///   한계 = μ0 ± L·σ·sqrt( λ/(2-λ) · (1-(1-λ)^(2i)) )   (초기 구간도 정확한 시변 한계)
/// 기본값 λ=0.2, L=3 (Montgomery, Introduction to Statistical Quality Control 권장 범위).
/// </summary>
public sealed class EwmaChart
{
    private readonly Baseline _baseline;
    private readonly double _lambda;
    private readonly double _l;
    private double _z;
    private int _i;

    public EwmaChart(Baseline baseline, double lambda = 0.2, double l = 3.0)
    {
        if (lambda is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(lambda));
        if (l <= 0) throw new ArgumentOutOfRangeException(nameof(l));
        _baseline = baseline;
        _lambda = lambda;
        _l = l;
        _z = baseline.Mean;
    }

    public EwmaPoint Add(double x)
    {
        _i++;
        _z = _lambda * x + (1 - _lambda) * _z;
        var width = _l * _baseline.StdDev *
                    Math.Sqrt(_lambda / (2 - _lambda) * (1 - Math.Pow(1 - _lambda, 2 * _i)));
        return new EwmaPoint(_i, x, _z, _baseline.Mean - width, _baseline.Mean + width);
    }
}

public sealed record CusumPoint(int Index, double Value, double Upper, double Lower, double DecisionInterval)
{
    public bool UpperSignal => Upper > DecisionInterval;
    public bool LowerSignal => Lower > DecisionInterval;
}

/// <summary>
/// 표 형식(tabular) CUSUM. 누적합으로 지속적인 한쪽 방향 이동을 잡는다.
///   C+_i = max(0, x_i - (μ0 + K) + C+_{i-1})
///   C-_i = max(0, (μ0 - K) - x_i + C-_{i-1})
///   K = k·σ, H = h·σ, C가 H를 넘으면 신호.  기본 k=0.5, h=5.
/// </summary>
public sealed class CusumChart
{
    private readonly Baseline _baseline;
    private readonly double _k;
    private readonly double _h;
    private double _upper;
    private double _lower;
    private int _i;

    public CusumChart(Baseline baseline, double k = 0.5, double h = 5.0)
    {
        _baseline = baseline;
        _k = k * baseline.StdDev;
        _h = h * baseline.StdDev;
    }

    public CusumPoint Add(double x)
    {
        _i++;
        _upper = Math.Max(0, x - (_baseline.Mean + _k) + _upper);
        _lower = Math.Max(0, (_baseline.Mean - _k) - x + _lower);
        return new CusumPoint(_i, x, _upper, _lower, _h);
    }
}
