using WeldTrace.Core.Spc;

namespace WeldTrace.Core.Tests;

public class ControlChartTests
{
    private static readonly Baseline Unit = new(10.0, 1.0, 30);

    [Fact]
    public void Baseline_uses_sample_standard_deviation()
    {
        // 2,4,4,4,5,5,7,9: 평균 5, 편차제곱합 32, n-1=7 → 분산 4.571429, 표준편차 2.138090
        var b = Baseline.FromSamples([2, 4, 4, 4, 5, 5, 7, 9]);
        Assert.Equal(5.0, b.Mean, 6);
        Assert.Equal(2.138090, b.StdDev, 6);
        Assert.Equal(8, b.Count);
    }

    [Fact]
    public void Baseline_rejects_too_few_or_constant_samples()
    {
        Assert.Throws<ArgumentException>(() => Baseline.FromSamples([1.0]));
        Assert.Throws<ArgumentException>(() => Baseline.FromSamples([3.0, 3.0, 3.0]));
    }

    [Fact]
    public void Ewma_values_and_exact_limits_match_hand_calculation()
    {
        // λ=0.2, L=3, μ0=10, σ=1
        // z1 = 0.2·10 + 0.8·10 = 10      폭1 = 3·sqrt(0.2/1.8·(1-0.8^2)) = 3·sqrt(0.04)  = 0.6
        // z2 = 0.2·12 + 0.8·10 = 10.4    폭2 = 3·sqrt(0.2/1.8·(1-0.8^4)) = 3·sqrt(0.0656) = 0.768375
        // z3 = 0.2·8 + 0.8·10.4 = 9.92
        var chart = new EwmaChart(Unit);
        var p1 = chart.Add(10);
        var p2 = chart.Add(12);
        var p3 = chart.Add(8);

        Assert.Equal(10.0, p1.Ewma, 9);
        Assert.Equal(10.6, p1.Ucl, 9);
        Assert.Equal(9.4, p1.Lcl, 9);
        Assert.Equal(10.4, p2.Ewma, 9);
        Assert.Equal(10.768375, p2.Ucl, 6);
        Assert.Equal(9.92, p3.Ewma, 9);
        Assert.False(p3.OutOfControl);
    }

    [Fact]
    public void Ewma_signals_sustained_downward_shift()
    {
        // 10, 7, 7 → z2 = 9.4 (LCL2 = 9.231625, 정상), z3 = 0.2·7 + 0.8·9.4 = 8.92
        // 폭3 = 3·sqrt(0.2/1.8·(1-0.8^6)) = 3·sqrt(0.081984) = 0.858985 → LCL3 = 9.141015 → 신호
        var chart = new EwmaChart(Unit);
        chart.Add(10);
        var p2 = chart.Add(7);
        var p3 = chart.Add(7);

        Assert.Equal(9.231625, p2.Lcl, 6);
        Assert.False(p2.BelowLcl);
        Assert.Equal(8.92, p3.Ewma, 9);
        Assert.Equal(9.141015, p3.Lcl, 6);
        Assert.True(p3.BelowLcl);
    }

    [Fact]
    public void Ewma_rejects_invalid_parameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaChart(Unit, lambda: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaChart(Unit, lambda: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EwmaChart(Unit, l: 0));
    }

    [Fact]
    public void Cusum_accumulates_and_signals_only_above_decision_interval()
    {
        // K = 0.5, H = 5. 값이 8로 계속 낮을 때 C- = 0.5, 2.0, 3.5, 5.0, 6.5 (첫 값은 9)
        // 5.0은 H와 같으므로 아직 신호가 아니고, 6.5에서 신호
        var chart = new CusumChart(Unit);
        var points = new[] { 9.0, 8, 8, 8, 8 }.Select(chart.Add).ToList();

        Assert.Equal(new[] { 0.5, 2.0, 3.5, 5.0, 6.5 }, points.Select(p => Math.Round(p.Lower, 9)));
        Assert.All(points, p => Assert.Equal(0.0, p.Upper));
        Assert.Equal(new[] { false, false, false, false, true }, points.Select(p => p.LowerSignal));
    }

    [Fact]
    public void Cusum_resets_to_zero_when_process_returns()
    {
        var chart = new CusumChart(Unit);
        chart.Add(8);                 // C- = 1.5
        var back = chart.Add(12);     // C- = max(0, 9.5 - 12 + 1.5) = 0, C+ = max(0, 12 - 10.5) = 1.5
        Assert.Equal(0.0, back.Lower, 9);
        Assert.Equal(1.5, back.Upper, 9);
    }
}
