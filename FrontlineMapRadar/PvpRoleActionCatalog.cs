using System.Collections.Generic;

namespace FrontlineMapRadar;

/// <summary>
/// PvP 职能技能目录（灰机 Wiki 各职业 PvP「职能技能」栏）。
/// 每职能三选一携带；ID 来自灰机 Wiki 技能编号。
/// </summary>
internal static class PvpRoleActionCatalog
{
    public readonly record struct Entry(uint ActionId, string Name, JobRole Role);

    /// <summary>远法 43252–43254 按 ID 连续段推断（坦克/近战/远敏/治疗已由 Wiki 确认）。</summary>
    public static readonly Entry[] All =
    [
        new(43243, "暴怒", JobRole.Tank),
        new(43244, "铁壁", JobRole.Tank),
        new(43245, "全力挥打", JobRole.Tank),

        new(43246, "浴血", JobRole.Melee),
        new(43247, "敏捷", JobRole.Melee),
        new(43248, "猛击", JobRole.Melee),

        new(43249, "速度之星", JobRole.PhysicalRanged),
        new(43250, "勇气", JobRole.PhysicalRanged),
        new(43251, "锐眼追击", JobRole.PhysicalRanged),

        new(43252, "彗星", JobRole.MagicalRanged),
        new(43253, "幻影弹", JobRole.MagicalRanged),
        new(43254, "武装削弱", JobRole.MagicalRanged),

        new(43255, "疗愈", JobRole.Healer),
        new(43256, "坚石肤", JobRole.Healer),
        new(43257, "溃破", JobRole.Healer),
    ];

    private static readonly Dictionary<uint, Entry> ById = BuildById();

    private static Dictionary<uint, Entry> BuildById()
    {
        var map = new Dictionary<uint, Entry>(All.Length);
        foreach (var e in All)
            map[e.ActionId] = e;
        return map;
    }

    public static bool TryGet(uint actionId, out Entry entry) => ById.TryGetValue(actionId, out entry);

    public static bool IsRoleAction(uint actionId) => ById.ContainsKey(actionId);

    public static string? GetName(uint actionId) =>
        ById.TryGetValue(actionId, out var e) ? e.Name : null;

    /// <summary>该职能可选的 3 个技能（按 Wiki 顺序）。</summary>
    public static Entry[] ForRole(JobRole role)
    {
        var list = new List<Entry>(3);
        foreach (var e in All)
        {
            if (e.Role == role)
                list.Add(e);
        }

        return list.ToArray();
    }
}
