using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 纷争前线个人极限槽：灰机 Wiki 7.55 职业积蓄时间 + PvP 极限技 / 诗人幻想曲 / 拂晓舞 ID。
/// </summary>
internal static class FrontlineLbCatalog
{
    public readonly record struct JobCharge(float ChargeSec, float FrontlineDeltaSec);

    /// <summary>有效积满时间（秒）= ChargeSec + FrontlineDeltaSec。</summary>
    public static float EffectiveChargeSec(in JobCharge job) =>
        Math.Max(1f, job.ChargeSec + job.FrontlineDeltaSec);

    /// <summary>名次对积蓄速率：第一 ×0.75、第二 ×1、第三 ×1.25。</summary>
    public static float RankRateMultiplier(int rank) =>
        rank switch
        {
            3 => 1.25f,
            2 => 1f,
            _ => 0.75f, // 未知按第一名（偏慢、偏保守）
        };

    // 诗人「英雄的幻想曲」：30s 内约额外积 15s 槽（小队、战斗中、30 码）
    public const float FantasiaDurationSec = 30f;
    public const float FantasiaExtraChargeSec = 15f;
    public const float FantasiaRangeYalms = 30f;

    // 拂晓舞 Wiki 未给具体百分比；按技能描述给自身/舞伴一段较短加速
    public const float DawnDurationSec = 15f;
    public const float DawnExtraChargeSec = 10f;

    /// <summary>PvP 极限技 ActionId（施放后个人槽归零）。不含幻想曲后续「光之安可」。</summary>
    public static readonly HashSet<uint> LimitBreakActionIds =
    [
        29069, // 骑士 盾阵
        29083, // 战士 原初的怒叫
        29097, // 暗骑 夜鸮
        29130, // 绝枪 无情连打
        29230, // 白魔 涤罪之心
        41502, // 学者 炽天附体
        29255, // 占星 星河漫天
        29266, // 贤者 中庸之道
        29485, // 武僧 陨石流星
        29497, // 龙骑 冲天
        29515, // 忍者 星遁天诛
        29537, // 武士 斩铁剑
        29553, // 钐镰 暗夜游魂
        39190, // 蝰蛇 世界吞噬者
        29401, // 诗人 英雄的幻想曲
        29415, // 机工 魔弹射手
        29432, // 舞者 行列舞
        29662, // 黑魔 灵魂共鸣
        29673, // 召唤 龙神召唤
        29678, // 召唤 不死鸟召唤
        41498, // 赤魔 南方十字
        39215, // 绘灵 冒险绘制
    ];

    public static readonly HashSet<uint> FantasiaActionIds = [29401];
    public static readonly HashSet<uint> DawnDanceActionIds = [];
    public static readonly HashSet<uint> FantasiaStatusIds = [];
    public static readonly HashSet<uint> DawnBoostStatusIds = [];

    private static readonly Dictionary<uint, JobCharge> ByJob = new()
    {
        [19] = new(135, 15),  // 骑士
        [21] = new(90, 0),    // 战士
        [32] = new(105, 45),  // 暗骑
        [37] = new(60, 15),   // 绝枪
        [24] = new(60, 15),   // 白魔
        [28] = new(90, 0),    // 学者
        [33] = new(120, 15),  // 占星
        [40] = new(120, 0),   // 贤者
        [20] = new(75, 0),    // 武僧
        [22] = new(90, 15),   // 龙骑
        [30] = new(105, 15),  // 忍者
        [34] = new(120, 15),  // 武士
        [39] = new(75, 15),   // 钐镰
        [41] = new(90, 0),    // 蝰蛇
        [23] = new(120, -15), // 诗人
        [31] = new(90, 0),    // 机工
        [38] = new(90, 30),   // 舞者
        [25] = new(60, 0),    // 黑魔
        [27] = new(90, 15),   // 召唤
        [35] = new(90, 0),    // 赤魔
        [42] = new(105, 0),   // 绘灵
        [1] = new(135, 15),
        [3] = new(90, 0),
        [2] = new(75, 0),
        [4] = new(90, 15),
        [5] = new(120, -15),
        [6] = new(60, 15),
        [7] = new(60, 0),
        [26] = new(90, 15),
        [29] = new(105, 15),
    };

    public static bool TryGetJob(uint classJobId, out JobCharge job) =>
        ByJob.TryGetValue(classJobId, out job);

    public static bool IsLimitBreak(uint actionId) => LimitBreakActionIds.Contains(actionId);

    public static bool IsFantasiaAction(uint actionId) => FantasiaActionIds.Contains(actionId);

    public static bool IsDawnDanceAction(uint actionId) => DawnDanceActionIds.Contains(actionId);

    public static bool IsFantasiaStatus(uint statusId) => FantasiaStatusIds.Contains(statusId);

    public static bool IsDawnBoostStatus(uint statusId) => DawnBoostStatusIds.Contains(statusId);

    public static bool IsAnyBoostStatus(uint statusId) =>
        IsFantasiaStatus(statusId) || IsDawnBoostStatus(statusId);

    /// <summary>用 Lumina Action/Status 表按中文/英文名补全诗人幻想曲与拂晓舞 ID。</summary>
    public static void InitializeFromSheets(IDataManager data, IPluginLog log)
    {
        try
        {
            var actions = data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (actions != null)
            {
                foreach (var row in actions)
                {
                    var name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var pvp = row.IsPvP;
                    if (NameIsFantasia(name) && pvp)
                        FantasiaActionIds.Add(row.RowId);

                    if (NameIsDawnDance(name) && pvp)
                        DawnDanceActionIds.Add(row.RowId);
                }
            }

            var statuses = data.GetExcelSheet<Status>();
            if (statuses != null)
            {
                foreach (var row in statuses)
                {
                    var name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (NameIsFantasia(name))
                        FantasiaStatusIds.Add(row.RowId);

                    if (NameIsDawnDance(name) || NameIsDawnBoostBuff(name))
                        DawnBoostStatusIds.Add(row.RowId);
                }
            }

            log.Information(
                "LB catalog: Fantasia actions {0}, Dawn actions {1}, Fantasia status {2}, Dawn status {3}",
                FantasiaActionIds.Count,
                DawnDanceActionIds.Count,
                FantasiaStatusIds.Count,
                DawnBoostStatusIds.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to resolve PvP LB / Dawn Dance IDs from Excel sheets");
        }
    }

    private static bool NameIsFantasia(string name) =>
        name.Contains("英雄的幻想曲", StringComparison.Ordinal) ||
        name.Contains("Final Fantasia", StringComparison.OrdinalIgnoreCase);

    private static bool NameIsDawnDance(string name) =>
        name.Contains("Dance of the Dawn", StringComparison.OrdinalIgnoreCase) ||
        (name.Contains("拂晓", StringComparison.Ordinal) && name.Contains("舞", StringComparison.Ordinal));

    private static bool NameIsDawnBoostBuff(string name) =>
        name.Contains("拂晓", StringComparison.Ordinal);
}
