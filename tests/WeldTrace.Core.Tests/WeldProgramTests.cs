using WeldTrace.Core.Domain;

namespace WeldTrace.Core.Tests;

public class WeldProgramTests
{
    private readonly WeldProgram _program = WeldProgram.BatteryModuleDefault();

    [Fact]
    public void Default_program_has_39_steps()
    {
        Assert.Equal(39, _program.StepCount);
    }

    [Theory]
    [InlineData(1, 82)]
    [InlineData(2, 83)]
    [InlineData(12, 83)]
    [InlineData(13, 38)]
    [InlineData(18, 38)]
    [InlineData(19, 83)]
    [InlineData(20, 82)]
    [InlineData(30, 82)]
    [InlineData(31, 38)]
    [InlineData(39, 38)]
    public void Step_set_power_matches_observed_pattern(int page, int expected)
    {
        Assert.Equal(expected, _program.ExpectedSetPower(page));
    }

    [Fact]
    public void Step_counts_per_set_power_match_real_data()
    {
        // 원본 학습 데이터: 38 → 15스텝, 82 → 12스텝, 83 → 12스텝
        var counts = _program.Steps.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(15, counts[38]);
        Assert.Equal(12, counts[82]);
        Assert.Equal(12, counts[83]);
    }

    [Fact]
    public void Nominal_for_step_uses_step_set_power()
    {
        Assert.Equal(1699.0, _program.NominalForStep(1));
        Assert.Equal(1721.0, _program.NominalForStep(2));
        Assert.Equal(692.0, _program.NominalForStep(14));
    }

    [Fact]
    public void Unknown_step_throws()
    {
        Assert.False(_program.IsValidStep(40));
        Assert.Throws<ArgumentOutOfRangeException>(() => _program.ExpectedSetPower(40));
    }

    [Fact]
    public void Program_requires_nominal_power_for_every_set_power()
    {
        var steps = new Dictionary<int, int> { [1] = 82, [2] = 99 };
        var nominal = new Dictionary<int, double> { [82] = 1699 };
        Assert.Throws<ArgumentException>(() => new WeldProgram("X", steps, nominal));
    }

    [Fact]
    public void Program_requires_consecutive_steps()
    {
        var steps = new Dictionary<int, int> { [1] = 82, [3] = 82 };
        var nominal = new Dictionary<int, double> { [82] = 1699 };
        Assert.Throws<ArgumentException>(() => new WeldProgram("X", steps, nominal));
    }
}
