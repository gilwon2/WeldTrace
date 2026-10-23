using WeldTrace.Core.Ingest;

namespace WeldTrace.Core.Tests;

public class ReplaySourceTests
{
    private static ReplaySource Load(params string[] lines) =>
        ReplaySource.Load(new StringReader(string.Join("\n", new[] { TestData.Header }.Concat(lines))));

    private const string A = "1,250,241.1,1699,1000,100,82,1154, 2026-01-05 09:00:00.000";
    private const string B = "2,250,241.2,1721,1000,100,83,1670, 2026-01-05 09:00:03.000";
    private const string C = "3,250,241.1,1699,1000,100,82,1154, 2026-01-05 09:00:06.000";

    [Fact]
    public void Emits_rows_up_to_virtual_now_in_file_order_without_changing_them()
    {
        var source = Load(A, B, C);

        Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0), source.Start);
        Assert.Equal(new[] { A }, source.TakeUntil(source.Start));
        Assert.Empty(source.TakeUntil(source.Start.AddSeconds(2)));
        Assert.Equal(new[] { B, C }, source.TakeUntil(source.Start.AddSeconds(6)));
        Assert.True(source.Completed);
        Assert.Equal(3, source.EmittedRows);
    }

    [Fact]
    public void Time_reversed_row_travels_with_its_neighbours()
    {
        // 두 번째 행의 시각이 8분 거꾸로 감 → 순서상 도달했을 때 함께 나가야 한다.
        const string reversed = "2,250,241.2,1721,1000,100,83,1670, 2026-01-05 08:52:03.000";
        var source = Load(A, reversed, C);

        Assert.Equal(new[] { A, reversed }, source.TakeUntil(source.Start.AddSeconds(1)));
        Assert.Equal(new[] { C }, source.TakeUntil(source.Start.AddSeconds(6)));
    }

    [Fact]
    public void Row_with_unreadable_time_follows_previous_row()
    {
        const string broken = "2,250,241.2,,1000,100,83,1670,";
        var source = Load(A, broken, C);

        Assert.Equal(new[] { A, broken }, source.TakeUntil(source.Start));
    }

    [Fact]
    public void Virtual_clock_applies_speed()
    {
        var source = Load(A, B, C);
        // 배속 60: 실제 0.1초 = 6초
        Assert.Equal(source.Start.AddSeconds(6), source.VirtualNow(TimeSpan.FromMilliseconds(100), 60));
    }

    [Fact]
    public void Header_without_time_column_is_rejected()
    {
        Assert.Throws<WeldFileFormatException>(() => ReplaySource.Load(new StringReader("PageNo,RealPower\n1,1699")));
    }
}
