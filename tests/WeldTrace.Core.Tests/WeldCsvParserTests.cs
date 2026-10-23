using WeldTrace.Core.Domain;
using WeldTrace.Core.Ingest;

namespace WeldTrace.Core.Tests;

public class WeldCsvParserTests
{
    private static ParseResult Parse(params string[] dataLines) =>
        WeldCsvParser.Parse(new StringReader(string.Join("\n", new[] { TestData.Header }.Concat(dataLines))));

    [Fact]
    public void Parses_original_format_with_leading_spaces()
    {
        // 원본 Training_Data.csv 첫 행과 같은 형식
        var result = Parse("1,250,241.1,1688,1000,100,82,1154, 2022-01-08 10:11:45.181");

        var row = Assert.Single(result.Rows);
        Assert.Equal(2, row.Line);
        Assert.Equal(new WeldRecord(1, 250, 241.1m, 1688, 1000, 100, 82, 1154,
            new DateTime(2022, 1, 8, 10, 11, 45, 181)), row.Record);
        Assert.Empty(result.Issues);
        Assert.Equal(1, result.DataLineCount);
    }

    [Fact]
    public void Missing_required_column_rejects_file()
    {
        var csv = "PageNo,Speed,Length,RealPower,SetFrequency,SetDuty,SetPower,GateOnTime\n1,250,241.1,1688,1000,100,82,1154";
        var ex = Assert.Throws<WeldFileFormatException>(() => WeldCsvParser.Parse(new StringReader(csv)));
        Assert.Contains("WorkingTime", ex.Message);
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        Assert.Throws<WeldFileFormatException>(() => WeldCsvParser.Parse(new StringReader("")));
    }

    [Fact]
    public void Header_is_case_insensitive_and_bom_tolerant()
    {
        var csv = "﻿pageno,speed,length,realpower,setfrequency,setduty,setpower,gateontime,workingtime\n" +
                  "1,250,241.1,1688,1000,100,82,1154,2022-01-08 10:11:45";
        var result = WeldCsvParser.Parse(new StringReader(csv));
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Column_count_mismatch_is_P1()
    {
        var result = Parse("1,250,241.1,1688,1000,100,82");
        Assert.Empty(result.Rows);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("P1", issue.Code);
        Assert.Equal(2, issue.Line);
        Assert.Equal(IssueLevel.Error, issue.Level);
    }

    [Fact]
    public void Blank_value_is_P2_and_names_the_column()
    {
        var result = Parse("1,250,241.1,,1000,100,82,1154, 2022-01-08 10:11:45.181");
        var issue = Assert.Single(result.Issues);
        Assert.Equal("P2", issue.Code);
        Assert.Contains("RealPower", issue.Message);
    }

    [Fact]
    public void Bad_number_or_date_is_P3()
    {
        var result = Parse(
            "1,250,241.1,abc,1000,100,82,1154, 2022-01-08 10:11:45.181",
            "2,250,241.1,1700,1000,100,83,1670, 2022/01/08 10:11:48");
        Assert.Empty(result.Rows);
        Assert.Equal(new[] { "P3", "P3" }, result.Issues.Select(i => i.Code));
        Assert.Contains("RealPower", result.Issues[0].Message);
        Assert.Contains("WorkingTime", result.Issues[1].Message);
    }

    [Fact]
    public void Blank_lines_are_ignored_but_line_numbers_follow_file()
    {
        var result = Parse(
            "1,250,241.1,1688,1000,100,82,1154, 2022-01-08 10:11:45.181",
            "",
            "2,250,241.2,1713,1000,100,83,1670, 2022-01-08 10:11:48.487");
        Assert.Equal(new[] { 2, 4 }, result.Rows.Select(r => r.Line));
        Assert.Equal(2, result.DataLineCount);
    }
}
