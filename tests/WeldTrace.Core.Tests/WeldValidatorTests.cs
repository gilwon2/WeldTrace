using WeldTrace.Core.Domain;
using WeldTrace.Core.Ingest;
using WeldTrace.Core.Validation;

namespace WeldTrace.Core.Tests;

public class WeldValidatorTests
{
    private readonly WeldValidator _validator = new(TestData.Program);

    private static ParsedWeld Row(int line, WeldRecord record) => new(line, record);

    [Fact]
    public void Clean_rows_pass_without_issues()
    {
        var rows = TestData.Module(TestData.T0).Select((w, i) => Row(i + 2, w)).ToList();
        var result = _validator.Validate(rows);
        Assert.Equal(39, result.Accepted.Count);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Exact_duplicate_keeps_first_and_warns_D1()
    {
        var w = TestData.Weld(1);
        var result = _validator.Validate([Row(2, w), Row(3, w)]);

        Assert.Equal(2, Assert.Single(result.Accepted).Line);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(("D1", 3, IssueLevel.Warning), (issue.Code, issue.Line!.Value, issue.Level));
    }

    [Fact]
    public void Same_time_with_different_values_excludes_all_D2()
    {
        var a = TestData.Weld(1, realPower: 1699);
        var b = a with { RealPower = 1500 };
        var c = TestData.Weld(2);
        var result = _validator.Validate([Row(2, a), Row(3, b), Row(4, c)]);

        Assert.Equal(4, Assert.Single(result.Accepted).Line);
        Assert.Equal(new[] { 2, 3 }, result.Issues.Where(i => i.Code == "D2").Select(i => i.Line!.Value));
    }

    [Fact]
    public void Step_outside_program_is_R1()
    {
        var result = _validator.Validate([Row(2, TestData.Weld(1) with { PageNo = 40 })]);
        Assert.Empty(result.Accepted);
        Assert.Equal("R1", Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2501)]
    public void Power_outside_physical_range_is_R2(int power)
    {
        var result = _validator.Validate([Row(2, TestData.Weld(1, realPower: power))]);
        Assert.Empty(result.Accepted);
        Assert.Equal("R2", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Zero_power_is_not_a_data_error()
    {
        // 출력 0은 설비 이상(미출력)이므로 판정 단계로 넘겨야 한다.
        var result = _validator.Validate([Row(2, TestData.Weld(1, realPower: 0))]);
        Assert.Single(result.Accepted);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Earlier_timestamp_is_kept_with_T1_warning()
    {
        var first = TestData.Weld(1, at: TestData.T0.AddSeconds(10));
        var back = TestData.Weld(2, at: TestData.T0); // 직전보다 10초 이름
        var next = TestData.Weld(3, at: TestData.T0.AddSeconds(5)); // 최댓값(10초)보다 여전히 이름
        var result = _validator.Validate([Row(2, first), Row(3, back), Row(4, next)]);

        Assert.Equal(3, result.Accepted.Count);
        Assert.Equal(new[] { 3, 4 }, result.Issues.Where(i => i.Code == "T1").Select(i => i.Line!.Value));
    }
}
