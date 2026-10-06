using System.Collections.Generic;

namespace FrontlineMapRadar;

/// <summary>
/// 各前线三处出生台在「游戏地图 UV（0–1）」中的大致位置。
/// 坐标由羊皮纸底图几何估算，并按默认校准反推到与 WorldToMap01 同一空间；
/// 阵营与台子的对应关系每场可能轮换，由 RadarFeedService 按玩家分布动态匹配。
/// </summary>
internal static class FrontlineSpawnPads
{
    // 羊皮纸显示 UV → 游戏地图 UV（默认校准 Offset=0.048 Scale=0.904）
    private static float G(float display) => (display - 0.048f) / 0.904f;

    private static readonly Dictionary<string, (float X, float Y)[]> PadsByMapKey = new()
    {
        // 周边遗迹：西 / 东 / 南（游戏地图 UV，底图无羊皮纸边框）
        ["376"] =
        [
            (0.28f, 0.36f),
            (0.72f, 0.36f),
            (0.50f, 0.78f),
        ],
        ["554"] =
        [
            (G(0.31f), G(0.31f)),
            (G(0.69f), G(0.31f)),
            (G(0.50f), G(0.74f)),
        ],
        // 尘封秘岩：北 / 西南 / 东南
        ["633"] =
        [
            (G(0.54f), G(0.30f)),
            (G(0.30f), G(0.64f)),
            (G(0.70f), G(0.66f)),
        ],
        // 昂萨哈凯尔：NW / NE / 南
        ["888"] =
        [
            (G(0.27f), G(0.36f)),
            (G(0.73f), G(0.36f)),
            (G(0.50f), G(0.77f)),
        ],
        // 沃戈尔·奇尔特：上 / 西南 / 东南
        ["worqor"] =
        [
            (G(0.50f), G(0.28f)),
            (G(0.28f), G(0.70f)),
            (G(0.72f), G(0.70f)),
        ],
    };

    public static (float X, float Y)[]? ForTerritory(uint territoryId, string? placeName = null)
    {
        var key = FrontlineMapAssets.ResolveMapKey(territoryId, placeName);
        return PadsByMapKey.TryGetValue(key, out var pads) ? pads : null;
    }
}
