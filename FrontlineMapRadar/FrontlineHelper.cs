using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 纷争前线区域判定。优先用已知 TerritoryId，再用 IntendedUse / 地名 / ContentType 兜底。
/// 周边遗迹（阵地战）的 ContentFinder ContentType 是 6（PvP）而不是 16，不能只认 16。
/// </summary>
internal static class FrontlineHelper
{
    private static readonly HashSet<uint> KnownFrontlineTerritories =
    [
        376,  // 周边遗迹群（阵地战）The Borderland Ruins (Secure)
        554,  // 荣誉野（碎冰战）Fields of Glory (Shatter)
        633,  // 尘封秘岩（争夺战）Seal Rock (Seize)
        888,  // 昂萨哈凯尔（竞争战）Onsal Hakair (Danshig Naadam)
        1313, // 沃戈尔·奇尔特 / 沃克奇特（凯旋战）Worqor Chirteh (Triumph)
    ];

    // ContentFinderCondition.ContentType：部分前线为 16；周边遗迹等老图为 6（PvP）
    private const uint FrontlineContentType = 16;
    private const uint PvpContentType = 6;
    // TerritoryIntendedUse：Frontline = 18
    private const uint FrontlineIntendedUse = 18;

    public static bool IsInFrontline(IClientState clientState, IDataManager dataManager)
    {
        if (!clientState.IsLoggedIn || clientState.TerritoryType == 0)
            return false;

        var territoryId = clientState.TerritoryType;
        if (KnownFrontlineTerritories.Contains(territoryId)
            || territoryId == FrontlineMapAssets.WorqorTerritoryId
            || territoryId == FrontlineMapAssets.SealRockTerritoryId
            || territoryId == FrontlineMapAssets.BorderlandTerritoryId)
        {
            RegisterKnownTerritory(territoryId);
            return true;
        }

        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        if (sheet == null || !sheet.TryGetRow(territoryId, out var territory))
            return false;

        var place = territory.PlaceName.IsValid
            ? territory.PlaceName.Value.Name.ToString()
            : string.Empty;
        var cfcName = string.Empty;
        if (territory.ContentFinderCondition.RowId != 0 && territory.ContentFinderCondition.IsValid)
            cfcName = territory.ContentFinderCondition.Value.Name.ToString();

        if (RegisterByPlaceName(territoryId, place) || RegisterByPlaceName(territoryId, cfcName))
            return true;

        if (TryGetIntendedUse(territory, out var intended) && intended == FrontlineIntendedUse)
        {
            RegisterByPlaceName(territoryId, place);
            return true;
        }

        if (territory.ContentFinderCondition.RowId != 0 &&
            territory.ContentFinderCondition.IsValid)
        {
            var contentType = territory.ContentFinderCondition.Value.ContentType.RowId;
            if (contentType == FrontlineContentType
                || (contentType == PvpContentType && LooksLikeAnyFrontlineName(place, cfcName)))
            {
                RegisterByPlaceName(territoryId, place);
                RegisterByPlaceName(territoryId, cfcName);
                return true;
            }
        }

        return false;
    }

    private static void RegisterKnownTerritory(uint territoryId)
    {
        if (territoryId == FrontlineMapAssets.WorqorTerritoryId)
            FrontlineMapAssets.RegisterWorqorTerritory(territoryId);
        else if (territoryId == FrontlineMapAssets.SealRockTerritoryId)
            FrontlineMapAssets.RegisterSealRockTerritory(territoryId);
        else if (territoryId == FrontlineMapAssets.BorderlandTerritoryId)
            FrontlineMapAssets.RegisterBorderlandTerritory(territoryId);
    }

    private static bool RegisterByPlaceName(uint territoryId, string? name)
    {
        if (FrontlineMapAssets.LooksLikeBorderlandName(name))
        {
            FrontlineMapAssets.RegisterBorderlandTerritory(territoryId);
            return true;
        }

        if (FrontlineMapAssets.LooksLikeSealRockName(name))
        {
            FrontlineMapAssets.RegisterSealRockTerritory(territoryId);
            return true;
        }

        if (FrontlineMapAssets.LooksLikeWorqorName(name))
        {
            FrontlineMapAssets.RegisterWorqorTerritory(territoryId);
            return true;
        }

        return false;
    }

    private static bool LooksLikeAnyFrontlineName(string place, string cfcName) =>
        FrontlineMapAssets.LooksLikeBorderlandName(place)
        || FrontlineMapAssets.LooksLikeBorderlandName(cfcName)
        || FrontlineMapAssets.LooksLikeSealRockName(place)
        || FrontlineMapAssets.LooksLikeSealRockName(cfcName)
        || FrontlineMapAssets.LooksLikeWorqorName(place)
        || FrontlineMapAssets.LooksLikeWorqorName(cfcName)
        || place.Contains("荣誉野", System.StringComparison.Ordinal)
        || place.Contains("昂萨哈凯尔", System.StringComparison.Ordinal)
        || cfcName.Contains("荣誉野", System.StringComparison.Ordinal)
        || cfcName.Contains("碎冰战", System.StringComparison.Ordinal)
        || cfcName.Contains("争夺战", System.StringComparison.Ordinal)
        || cfcName.Contains("竞争战", System.StringComparison.Ordinal)
        || cfcName.Contains("Frontline", System.StringComparison.OrdinalIgnoreCase)
        || cfcName.Contains("纷争前线", System.StringComparison.Ordinal);

    private static bool TryGetIntendedUse(in TerritoryType territory, out uint intendedUse)
    {
        intendedUse = 0;
        try
        {
            intendedUse = territory.TerritoryIntendedUse.RowId;
            return intendedUse != 0;
        }
        catch
        {
            return false;
        }
    }
}
