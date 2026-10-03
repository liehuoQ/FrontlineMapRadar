using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 纷争前线区域判定。优先用已知 TerritoryId，再用 ContentFinderCondition / IsPvpZone 兜底。
/// </summary>
internal static class FrontlineHelper
{
    // 常见纷争前线地图 TerritoryType.RowId
    private static readonly HashSet<uint> KnownFrontlineTerritories =
    [
        376,  // 周边遗迹群（阵地战）The Borderland Ruins (Secure)
        554,  // 荣誉野（碎冰战）Fields of Glory (Shatter)
        633,  // 尘封秘岩（争夺战）Seal Rock (Seize)
        888,  // 昂萨哈凯尔（竞争战）Onsal Hakair (Danshig Naadam)
        1313, // 沃戈尔·奇尔特 / 沃克奇特（凯旋战）Worqor Chirteh (Triumph)
    ];

    // ContentFinderCondition.ContentType：纷争前线一般为 16
    private const uint FrontlineContentType = 16;

    public static bool IsInFrontline(IClientState clientState, IDataManager dataManager)
    {
        if (!clientState.IsLoggedIn || clientState.TerritoryType == 0)
            return false;

        var territoryId = clientState.TerritoryType;
        if (KnownFrontlineTerritories.Contains(territoryId)
            || territoryId == FrontlineMapAssets.WorqorTerritoryId)
        {
            if (territoryId == FrontlineMapAssets.WorqorTerritoryId)
                FrontlineMapAssets.RegisterWorqorTerritory(territoryId);
            return true;
        }

        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        if (sheet == null || !sheet.TryGetRow(territoryId, out var territory))
            return false;

        // ContentFinderCondition → ContentType（含 7.4 沃戈尔·奇尔特等新前线）
        if (territory.ContentFinderCondition.RowId != 0 &&
            territory.ContentFinderCondition.IsValid)
        {
            var cfc = territory.ContentFinderCondition.Value;
            if (cfc.ContentType.RowId == FrontlineContentType)
                return true;
        }

        // 地名兜底：沃戈尔·奇尔特
        if (territory.PlaceName.IsValid &&
            FrontlineMapAssets.LooksLikeWorqorName(territory.PlaceName.Value.Name.ToString()))
        {
            FrontlineMapAssets.RegisterWorqorTerritory(territoryId);
            return true;
        }

        return false;
    }
}
