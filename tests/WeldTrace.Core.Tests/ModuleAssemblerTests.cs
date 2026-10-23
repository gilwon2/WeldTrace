using WeldTrace.Core.Modules;

namespace WeldTrace.Core.Tests;

public class ModuleAssemblerTests
{
    [Fact]
    public void Splits_modules_when_step_number_restarts()
    {
        var welds = TestData.Module(TestData.T0)
            .Concat(TestData.Module(TestData.T0.AddMinutes(5)))
            .ToList();

        var modules = ModuleAssembler.Assemble(welds, 39);

        Assert.Equal(2, modules.Count);
        Assert.All(modules, m => Assert.True(m.IsComplete));
        Assert.Equal(new[] { 1, 2 }, modules.Select(m => m.Sequence));
        Assert.Equal(TestData.T0, modules[0].StartTime);
        Assert.Equal(TestData.T0.AddSeconds(3 * 38), modules[0].EndTime);
    }

    [Fact]
    public void Missing_step_makes_module_incomplete()
    {
        var welds = TestData.Module(TestData.T0).Where(w => w.PageNo != 20).ToList();
        var module = Assert.Single(ModuleAssembler.Assemble(welds, 39));
        Assert.False(module.IsComplete);
        Assert.Equal(38, module.Welds.Count);
    }

    [Fact]
    public void Long_idle_gap_splits_even_without_restart()
    {
        // 스텝 1~10 후 1시간 멈췄다가 11~39가 이어지면 같은 모듈로 볼 수 없다.
        var welds = TestData.Module(TestData.T0)
            .Select(w => w.PageNo > 10 ? w with { WorkingTime = w.WorkingTime.AddHours(1) } : w)
            .ToList();

        var modules = ModuleAssembler.Assemble(welds, 39);

        Assert.Equal(new[] { 10, 29 }, modules.Select(m => m.Welds.Count));
        Assert.All(modules, m => Assert.False(m.IsComplete));
    }

    [Fact]
    public void Empty_input_gives_no_modules()
    {
        Assert.Empty(ModuleAssembler.Assemble([], 39));
    }
}
