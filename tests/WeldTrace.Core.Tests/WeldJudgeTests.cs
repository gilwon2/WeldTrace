using WeldTrace.Core.Judgment;
using WeldTrace.Core.Modules;

namespace WeldTrace.Core.Tests;

public class WeldJudgeTests
{
    private readonly WeldJudge _judge = new(TestData.Program);

    [Fact]
    public void Nominal_power_is_ok_with_ratio_one()
    {
        var j = _judge.Judge(TestData.Weld(1, realPower: 1699));
        Assert.Equal(1.0, j.PowerRatio, 6);
        Assert.Empty(j.Faults);
    }

    [Fact]
    public void Below_95_percent_is_power_drop()
    {
        // 1600 / 1699 = 0.941730... < 0.95
        var j = _judge.Judge(TestData.Weld(1, realPower: 1600));
        Assert.Equal(0.941730, j.PowerRatio, 6);
        Assert.Equal(new[] { FaultCode.PowerDrop }, j.Faults);
        Assert.True(j.IsNg);
    }

    [Fact]
    public void Exactly_threshold_is_not_power_drop()
    {
        // 657.4 / 692 = 0.95 → 정수 출력이므로 658W(0.950867)는 정상, 657W(0.949422)는 저하
        Assert.Empty(_judge.Judge(TestData.Weld(14, realPower: 658)).Faults);
        Assert.Equal(new[] { FaultCode.PowerDrop }, _judge.Judge(TestData.Weld(14, realPower: 657)).Faults);
    }

    [Fact]
    public void Zero_power_is_no_output_only()
    {
        var j = _judge.Judge(TestData.Weld(1, realPower: 0));
        Assert.Equal(new[] { FaultCode.NoOutput }, j.Faults);
        Assert.True(j.IsNg);
    }

    [Fact]
    public void Recipe_deviation_with_normal_output_is_warning_only()
    {
        // 원본 03일: 스텝 14 SetPower 35, 실측 681W → 검사 합격(라벨 0). 681/692 = 0.984104
        var j = _judge.Judge(TestData.Weld(14, setPower: 35, realPower: 681));
        Assert.Equal(new[] { FaultCode.RecipeDeviation }, j.Faults);
        Assert.False(j.IsNg);
    }

    [Fact]
    public void Recipe_deviation_with_low_output_is_ng()
    {
        // 원본 03일: 스텝 14 SetPower 35, 실측 630W → 불량(라벨 1). 630/692 = 0.910405
        var j = _judge.Judge(TestData.Weld(14, setPower: 35, realPower: 630));
        Assert.Equal(0.910405, j.PowerRatio, 6);
        Assert.Equal(new[] { FaultCode.PowerDrop, FaultCode.RecipeDeviation }, j.Faults);
        Assert.True(j.IsNg);
    }

    private ModuleJudgment JudgeModule(Func<Domain.WeldRecord, Domain.WeldRecord> change)
    {
        var welds = TestData.Module(TestData.T0).Select(change).ToList();
        return _judge.Judge(Assert.Single(ModuleAssembler.Assemble(welds, 39)));
    }

    [Fact]
    public void Clean_module_is_ok_and_reports_mean_ratio()
    {
        var m = JudgeModule(w => w);
        Assert.Equal(Verdict.Ok, m.Verdict);
        Assert.Null(m.PrimaryFault);
        Assert.Equal(1.0, m.MeanPowerRatio!.Value, 6);
    }

    [Fact]
    public void One_power_drop_makes_module_ng_and_excludes_it_from_mean()
    {
        var m = JudgeModule(w => w.PageNo == 5 ? w with { RealPower = 1000 } : w);
        Assert.Equal(Verdict.Ng, m.Verdict);
        Assert.Equal(FaultCode.PowerDrop, m.PrimaryFault);
        Assert.Equal(1.0, m.MeanPowerRatio!.Value, 6); // 나머지 38스텝은 공칭 그대로
    }

    [Fact]
    public void Deviation_only_module_is_warning()
    {
        var m = JudgeModule(w => w.PageNo == 14 ? w with { SetPower = 35, RealPower = 681 } : w);
        Assert.Equal(Verdict.Warning, m.Verdict);
        Assert.Equal(FaultCode.RecipeDeviation, m.PrimaryFault);
    }

    [Fact]
    public void No_output_outranks_power_drop()
    {
        var m = JudgeModule(w => w.PageNo switch
        {
            3 => w with { RealPower = 1000 },
            30 => w with { RealPower = 0 },
            _ => w,
        });
        Assert.Equal(FaultCode.NoOutput, m.PrimaryFault);
    }

    [Fact]
    public void Incomplete_module_is_hold_without_mean()
    {
        var welds = TestData.Module(TestData.T0).Where(w => w.PageNo != 39).ToList();
        var m = _judge.Judge(Assert.Single(ModuleAssembler.Assemble(welds, 39)));
        Assert.Equal(Verdict.Hold, m.Verdict);
        Assert.Null(m.MeanPowerRatio);
    }
}
