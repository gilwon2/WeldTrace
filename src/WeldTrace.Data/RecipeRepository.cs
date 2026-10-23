using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using WeldTrace.Core.Domain;

namespace WeldTrace.Data;

internal static class DapperSetup
{
    private static int _done;

    /// <summary>
    /// Dapper는 DateTime을 기본으로 SQL datetime(정밀도 3.33ms)으로 보낸다.
    /// 용접 시각은 datetime2(3)이고 유일키라서 반올림되면 안 되므로 datetime2로 보내게 한다.
    /// </summary>
    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
        SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
    }
}

/// <summary>레시피 마스터를 DB에서 읽어 판정용 WeldProgram으로 만든다.</summary>
public static class RecipeRepository
{
    private sealed class StepRow
    {
        public int PageNo { get; set; }
        public int SetPower { get; set; }
    }

    private sealed class PowerRow
    {
        public int SetPower { get; set; }
        public double NominalW { get; set; }
    }

    public static async Task<WeldProgram> LoadForEquipmentAsync(
        SqlConnection connection, string equipmentId, SqlTransaction? tx = null)
    {
        DapperSetup.Ensure();
        var recipeCode = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT recipe_code FROM dbo.equipment WHERE equipment_id = @equipmentId",
            new { equipmentId }, tx)
            ?? throw new InvalidOperationException($"등록되지 않은 설비입니다: {equipmentId}");

        var steps = await connection.QueryAsync<StepRow>(
            "SELECT page_no AS PageNo, set_power AS SetPower FROM dbo.recipe_step WHERE recipe_code = @recipeCode",
            new { recipeCode }, tx);
        var powers = await connection.QueryAsync<PowerRow>(
            "SELECT set_power AS SetPower, CAST(nominal_w AS float) AS NominalW FROM dbo.recipe_power WHERE recipe_code = @recipeCode",
            new { recipeCode }, tx);

        return new WeldProgram(
            recipeCode,
            steps.ToDictionary(s => s.PageNo, s => s.SetPower),
            powers.ToDictionary(p => p.SetPower, p => p.NominalW));
    }
}
