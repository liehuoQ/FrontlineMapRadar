using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace FrontlineMapRadar;

public sealed class RosterPlayer
{
    public ulong ContentId { get; init; }
    public string Name { get; set; } = string.Empty;
    public uint HomeWorldId { get; set; }
    public string HomeWorldName { get; set; } = string.Empty;
    public uint ClassJobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string JobAbbreviation { get; set; } = string.Empty;
    public JobRole Role { get; set; } = JobRole.Unknown;
    public string RoleName => RoleHelper.ToDisplayName(Role);

    /// <summary>当前实际携带的职能技能 ActionId；0 表示尚未观测到。</summary>
    public uint EquippedRoleActionId { get; set; }

    /// <summary>当前携带的职能技能中文名；未观测到时为空。</summary>
    public string EquippedRoleActionName { get; set; } = string.Empty;

    public string RoleActionsDisplay =>
        string.IsNullOrEmpty(EquippedRoleActionName) ? "未知" : EquippedRoleActionName;

    public FrontlineGc GrandCompany { get; set; } = FrontlineGc.Unknown;
    public string GrandCompanyName => FrontlineGcHelper.ToDisplayName(GrandCompany);

    /// <summary>场地标记索引 0-7（A/B/C/D/1/2/3/4），null 表示未标记。</summary>
    public int? MarkerIndex { get; set; }

    public bool SeenThisScan { get; set; }
    public bool CurrentlyLoaded { get; set; }
    public Vector3 LastPosition { get; set; }
    public uint EntityId { get; set; }

    public string MarkerLabel =>
        MarkerIndex is { } idx && idx >= 0 && idx < FieldMarkerLabels.Length
            ? FieldMarkerLabels[idx]
            : "无";

    public static readonly string[] FieldMarkerLabels = ["A", "B", "C", "D", "1", "2", "3", "4"];
}

public enum JobRole : byte
{
    Unknown = 0,
    Tank = 1,
    Melee = 2,
    PhysicalRanged = 3,
    MagicalRanged = 4,
    Healer = 5,
}

internal static class RoleHelper
{
    public static JobRole FromClassJobId(uint classJobId) =>
        classJobId switch
        {
            19 or 21 or 32 or 37 => JobRole.Tank,           // PLD WAR DRK GNB
            24 or 28 or 33 or 40 => JobRole.Healer,         // WHM SCH AST SGE
            20 or 22 or 30 or 34 or 39 or 41 => JobRole.Melee, // MNK DRG NIN SAM RPR VPR
            23 or 31 or 38 => JobRole.PhysicalRanged,      // BRD MCH DNC
            25 or 27 or 35 or 42 => JobRole.MagicalRanged,  // BLM SMN RDM PCT
            _ => JobRole.Unknown,
        };

    /// <summary>职业中文名（战士 / 舞者 / 骑士 …）</summary>
    public static string GetChineseJobName(uint classJobId, string? sheetName = null)
    {
        if (ChineseJobNames.TryGetValue(classJobId, out var cn))
            return cn;

        if (!string.IsNullOrWhiteSpace(sheetName) && ContainsCjk(sheetName))
            return sheetName;

        return string.IsNullOrWhiteSpace(sheetName) ? $"职业{classJobId}" : sheetName;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff')
                return true;
        }

        return false;
    }

    private static readonly Dictionary<uint, string> ChineseJobNames = new()
    {
        // 坦克
        [19] = "骑士",
        [21] = "战士",
        [32] = "暗黑骑士",
        [37] = "绝枪战士",
        // 治疗
        [24] = "白魔法师",
        [28] = "学者",
        [33] = "占星术士",
        [40] = "贤者",
        // 近战
        [20] = "武僧",
        [22] = "龙骑士",
        [30] = "忍者",
        [34] = "武士",
        [39] = "钐镰客",
        [41] = "蝰蛇剑士",
        // 远敏
        [23] = "吟游诗人",
        [31] = "机工士",
        [38] = "舞者",
        // 远法
        [25] = "黑魔法师",
        [27] = "召唤师",
        [35] = "赤魔法师",
        [42] = "绘灵法师",
        // 特职基职（少见，一并给出）
        [1] = "剑术师",
        [2] = "格斗家",
        [3] = "斧术师",
        [4] = "枪术师",
        [5] = "弓箭手",
        [6] = "幻术师",
        [7] = "咒术师",
        [26] = "秘术师",
        [29] = "双剑师",
    };

    /// <summary>
    /// 根据 ClassJob.Role（Lumina）兜底；若无则用职业 ID 表。
    /// Lumina Role: 1 Tank, 2 Melee, 3 Ranged, 4 Healer（部分版本合并远程）。
    /// </summary>
    public static JobRole FromClassJob(uint classJobId, byte luminaRole)
    {
        var fromId = FromClassJobId(classJobId);
        if (fromId != JobRole.Unknown)
            return fromId;

        return luminaRole switch
        {
            1 => JobRole.Tank,
            2 => JobRole.Melee,
            3 => JobRole.PhysicalRanged,
            4 => JobRole.Healer,
            _ => JobRole.Unknown,
        };
    }

    public static string ToDisplayName(JobRole role) =>
        role switch
        {
            JobRole.Tank => "坦克",
            JobRole.Healer => "治疗",
            JobRole.Melee => "近战",
            JobRole.PhysicalRanged => "远敏",
            JobRole.MagicalRanged => "远法",
            _ => "未知",
        };

    /// <summary>各职能可选的 3 个 PvP 职能技能名称（灰机 Wiki）。</summary>
    public static string[] GetPvpRoleActions(JobRole role)
    {
        var entries = PvpRoleActionCatalog.ForRole(role);
        var names = new string[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            names[i] = entries[i].Name;
        return names;
    }
}

internal static class FieldMarkerHelper
{
    public static unsafe bool TryPlace(int index, Vector3 position)
    {
        if (index is < 0 or > 7)
            return false;

        var controller = MarkingController.Instance();
        if (controller == null)
            return false;

        var pos = position;
        // 0 = Success；2 = 操作过频等
        return controller->PlaceFieldMarker((uint)index, &pos) == 0;
    }

    public static unsafe void Clear(int index)
    {
        if (index is < 0 or > 7)
            return;

        var controller = MarkingController.Instance();
        if (controller == null)
            return;

        controller->ClearFieldMarker((uint)index);
    }

    /// <summary>读取当前已放置的场景标记（A/B/C/D/1/2/3/4）。</summary>
    public static unsafe void CollectActive(List<(int Index, string Label, Vector3 Position)> into)
    {
        into.Clear();
        var controller = MarkingController.Instance();
        if (controller == null)
            return;

        for (var i = 0; i < 8; i++)
        {
            ref var marker = ref controller->FieldMarkers[i];
            if (!marker.Active)
                continue;

            var label = i < RosterPlayer.FieldMarkerLabels.Length
                ? RosterPlayer.FieldMarkerLabels[i]
                : (i + 1).ToString();
            into.Add((i, label, marker.Position));
        }
    }
}
