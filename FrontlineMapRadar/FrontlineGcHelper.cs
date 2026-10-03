using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace FrontlineMapRadar;

/// <summary>
/// 纷争前线三大国阵营。
/// 敌方姓名栏颜色类型：Lim=4 黑涡 / Gri=5 双蛇 / Uld=6 恒辉。
/// </summary>
public enum FrontlineGc : byte
{
    Unknown = 0,
    Maelstrom = 1, // 黑涡团
    TwinAdder = 2, // 双蛇党
    Flames = 3,    // 恒辉队
}

internal static class FrontlineGcHelper
{
    // GameObject.GetNamePlateColorType 文档中的 GC 相关索引
    private const byte NamePlateLim = 4; // 黑涡
    private const byte NamePlateGri = 5; // 双蛇
    private const byte NamePlateUld = 6; // 恒辉

    public static unsafe FrontlineGc FromNamePlateColorType(byte colorType) =>
        colorType switch
        {
            NamePlateLim => FrontlineGc.Maelstrom,
            NamePlateGri => FrontlineGc.TwinAdder,
            NamePlateUld => FrontlineGc.Flames,
            _ => FrontlineGc.Unknown,
        };

    public static unsafe byte GetNamePlateColorType(IGameObject obj)
    {
        var go = (GameObject*)obj.Address;
        return go == null ? (byte)0 : go->GetNamePlateColorType();
    }

    /// <summary>
    /// 解析玩家阵营：敌方直接读姓名栏 GC 色；小队/同盟/自己视为本阵营。
    /// </summary>
    public static FrontlineGc Resolve(IPlayerCharacter player, FrontlineGc ownGc)
    {
        var type = GetNamePlateColorType(player);
        var fromPlate = FromNamePlateColorType(type);
        if (fromPlate != FrontlineGc.Unknown)
            return fromPlate;

        // 1=Self, 2=Party, 20=Alliance —— 前线里本阵营通常用这些，而不是 GC 色
        return type is 1 or 2 or 20 ? ownGc : FrontlineGc.Unknown;
    }

    /// <summary>
    /// 通过已看见的敌方 GC 反推本阵营（两敌方 GC 已知时，剩下的就是己方）。
    /// </summary>
    public static FrontlineGc InferOwnGc(IObjectTable objectTable)
    {
        var hostile = new HashSet<FrontlineGc>();

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            var type = GetNamePlateColorType(player);
            var gc = FromNamePlateColorType(type);
            if (gc == FrontlineGc.Unknown)
                continue;

            // 只有姓名栏走 GC 色的，基本都是敌对阵营
            hostile.Add(gc);
        }

        if (hostile.Count != 2)
            return FrontlineGc.Unknown;

        if (!hostile.Contains(FrontlineGc.Maelstrom))
            return FrontlineGc.Maelstrom;
        if (!hostile.Contains(FrontlineGc.TwinAdder))
            return FrontlineGc.TwinAdder;
        if (!hostile.Contains(FrontlineGc.Flames))
            return FrontlineGc.Flames;

        return FrontlineGc.Unknown;
    }

    public static string ToDisplayName(FrontlineGc gc) =>
        gc switch
        {
            FrontlineGc.Maelstrom => "黑涡团",
            FrontlineGc.TwinAdder => "双蛇党",
            FrontlineGc.Flames => "恒辉队",
            _ => "未知",
        };
}
