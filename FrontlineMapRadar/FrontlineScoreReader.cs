using System;
using System.Collections.Generic;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FrontlineMapRadar;

/// <summary>
/// 从前线比分 Addon 读三家分数并换算名次。
/// 同分按较慢（名次靠前）计；开局 0-0-0 三家都算第一。
/// 读失败时 <see cref="FrontlineScores.Parsed"/> 为 false，调用方按第一名保守估计。
/// </summary>
internal sealed unsafe class FrontlineScoreReader
{
    private static readonly string[] AddonNames =
    [
        "PvPFrontline",
        "PvpFrontline",
        "PvPFrontlineScore",
        "FrontlineBattle",
        "PvPScore",
        "PvPMap",
        "PvPFrontlineHeader",
        "ScreenInfoPvPFrontline",
    ];

    private readonly Plugin plugin;
    private string? lastHitAddon;
    private bool loggedMiss;

    public FrontlineScoreReader(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public FrontlineScores Read(FrontlineGc ownGc)
    {
        foreach (var name in AddonNames)
        {
            var addon = plugin.GameGui.GetAddonByName(name);
            if (addon.IsNull)
                continue;

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || !unit->IsVisible)
                continue;

            if (TryParseUnit(unit, ownGc, out var scores))
            {
                if (lastHitAddon != name)
                {
                    lastHitAddon = name;
                    plugin.Log.Information("Frontline scores from addon {0}", name);
                }

                return scores;
            }
        }

        if (!loggedMiss)
        {
            loggedMiss = true;
            plugin.Log.Information("Frontline score addon not found; LB rank falls back to 1st (conservative).");
        }

        return FrontlineScores.Unknown;
    }

    /// <summary>名次 1/2/3；未知或未解析时返回 1。</summary>
    public static int RankFor(in FrontlineScores scores, FrontlineGc gc)
    {
        if (!scores.Parsed || gc == FrontlineGc.Unknown)
            return 1;

        var mine = scores.ScoreOf(gc);
        var rank = 1;
        if (scores.Maelstrom > mine)
            rank++;
        if (scores.TwinAdder > mine)
            rank++;
        if (scores.Flames > mine)
            rank++;
        return rank;
    }

    private static bool TryParseUnit(AtkUnitBase* unit, FrontlineGc ownGc, out FrontlineScores scores)
    {
        scores = default;
        var texts = new List<ScoreText>(16);
        CollectText(unit, texts);
        if (texts.Count == 0)
            return false;

        int? mael = null, adder = null, flames = null, own = null;
        var unlabeled = new List<int>(4);

        foreach (var t in texts)
        {
            if (!TryParseScore(t.Text, out var value))
                continue;

            var gc = GuessGcFromColor(t.R, t.G, t.B);
            if (t.Text.Contains('☆') || t.Text.Contains('★') || t.Text.Contains('*'))
                own = value;

            if (gc == FrontlineGc.Maelstrom)
                mael = value;
            else if (gc == FrontlineGc.TwinAdder)
                adder = value;
            else if (gc == FrontlineGc.Flames)
                flames = value;
            else
                unlabeled.Add(value);
        }

        if (mael is { } m && adder is { } a && flames is { } f)
        {
            scores = new FrontlineScores(m, a, f, true);
            return true;
        }

        // 只有己方分数（☆）+ 两个未着色分数：能算己方名次，敌方两家无法对应 GC
        if (own is { } ownScore && unlabeled.Count >= 2 && ownGc != FrontlineGc.Unknown)
        {
            var others = new List<int>(unlabeled.Count);
            var consumedOwn = false;
            foreach (var v in unlabeled)
            {
                if (!consumedOwn && v == ownScore)
                {
                    consumedOwn = true;
                    continue;
                }

                others.Add(v);
            }

            if (others.Count >= 2)
            {
                scores = FrontlineScores.FromOwnAndTwoEnemies(ownGc, ownScore, others[0], others[1]);
                return scores.Parsed;
            }
        }

        return false;
    }

    private static void CollectText(AtkUnitBase* unit, List<ScoreText> into)
    {
        var count = unit->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;

            var textNode = (AtkTextNode*)node;
            var text = textNode->NodeText.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            into.Add(new ScoreText(text.Trim(), textNode->TextColor.R, textNode->TextColor.G, textNode->TextColor.B));
        }
    }

    private static bool TryParseScore(string text, out int value)
    {
        value = 0;
        if (text.Contains(':') || text.Contains('：'))
            return false;

        var span = text.AsSpan();
        var start = -1;
        for (var i = 0; i < span.Length; i++)
        {
            if (char.IsDigit(span[i]))
            {
                if (start < 0)
                    start = i;
            }
            else if (start >= 0)
            {
                return int.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out value)
                       && value is >= 0 and < 100000;
            }
        }

        if (start < 0)
            return false;

        return int.TryParse(span[start..], NumberStyles.None, CultureInfo.InvariantCulture, out value)
               && value is >= 0 and < 100000;
    }

    private static FrontlineGc GuessGcFromColor(byte r, byte g, byte b)
    {
        if (r > 160 && g < 120 && b < 120)
            return FrontlineGc.Maelstrom;
        if (r > 150 && g > 140 && b < 120)
            return FrontlineGc.TwinAdder;
        if (b > 150 && r < 140 && g < 180)
            return FrontlineGc.Flames;
        return FrontlineGc.Unknown;
    }

    private readonly record struct ScoreText(string Text, byte R, byte G, byte B);
}

internal readonly record struct FrontlineScores(int Maelstrom, int TwinAdder, int Flames, bool Parsed)
{
    public static FrontlineScores Unknown => default;

    public int ScoreOf(FrontlineGc gc) =>
        gc switch
        {
            FrontlineGc.Maelstrom => Maelstrom,
            FrontlineGc.TwinAdder => TwinAdder,
            FrontlineGc.Flames => Flames,
            _ => 0,
        };

    /// <summary>
    /// 己方分数已知、另外两家分数已知但不知谁是谁时：
    /// 己方写入对应 GC；两敌按枚举顺序填剩余槽（仅己方名次可靠，敌方名次仍可用分数集合比较）。
    /// 敌方焦点：两敌分数互相对调不影响「这两个分数」的排序，但 GC 对应会错。
    /// 因此敌方两家都标为未绑定——这里改为不 Parsed 给敌方，只在调用方用 OwnRank。
    /// 简化：仍写入，敌方 GC 映射可能对调；HUD 对敌方在 Parsed 且三家齐全时显示名次。
    /// </summary>
    public static FrontlineScores FromOwnAndTwoEnemies(FrontlineGc ownGc, int own, int e1, int e2)
    {
        var m = 0;
        var a = 0;
        var f = 0;
        switch (ownGc)
        {
            case FrontlineGc.Maelstrom:
                m = own;
                a = e1;
                f = e2;
                break;
            case FrontlineGc.TwinAdder:
                a = own;
                m = e1;
                f = e2;
                break;
            case FrontlineGc.Flames:
                f = own;
                m = e1;
                a = e2;
                break;
            default:
                return Unknown;
        }

        return new FrontlineScores(m, a, f, true);
    }
}
