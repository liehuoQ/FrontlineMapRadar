using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 采集前线玩家世界坐标，换算成地图 0–1 坐标，供 HTTP 第二屏推送。
/// </summary>
public sealed class RadarFeedService : IDisposable
{
    private readonly Plugin plugin;
    private readonly object sync = new();
    private RadarFeedState latest = new();
    private readonly Dictionary<uint, MapMeta> mapByTerritory = new();
    private readonly List<(int Index, string Label, Vector3 Position)> markerScratch = [];
    private uint baseAssignTerritory;
    private FrontlineGc[]? basePadGcs; // length 3, sticky per instance

    private readonly record struct MapMeta(ushort SizeFactor, short OffsetX, short OffsetY, string Name);

    public RadarFeedService(Plugin plugin)
    {
        this.plugin = plugin;

        var sheet = plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (!row.Map.IsValid)
                    continue;

                var map = row.Map.Value;
                var name = row.PlaceName.IsValid ? row.PlaceName.Value.Name.ToString() : string.Empty;
                mapByTerritory[row.RowId] = new MapMeta(map.SizeFactor, map.OffsetX, map.OffsetY, name);
            }
        }

        plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        plugin.Framework.Update -= OnFrameworkUpdate;
    }

    internal RadarFeedState GetSnapshot()
    {
        lock (sync)
            return Clone(latest);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!plugin.Config.RemoteRadarEnabled)
        {
            PublishIdle();
            return;
        }

        if (!plugin.ClientState.IsLoggedIn)
        {
            PublishIdle();
            return;
        }

        var inFrontline = FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager);
        if (!inFrontline)
        {
            PublishIdle(territoryOnly: true);
            return;
        }

        var local = plugin.ObjectTable.LocalPlayer;
        if (local == null)
        {
            PublishIdle(territoryOnly: true);
            return;
        }

        var territoryId = plugin.ClientState.TerritoryType;
        mapByTerritory.TryGetValue(territoryId, out var meta);
        var ownGc = FrontlineGcHelper.InferOwnGc(plugin.ObjectTable);
        var hideOwn = plugin.Config.HideOwnTeam;
        var rosterByEntity = BuildRosterLookup();

        var players = new List<RadarFeedPlayer>();
        RadarFeedPlayer? self = null;

        foreach (var obj in plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;
            if (player.ObjectKind != ObjectKind.Pc)
                continue;

            var isSelf = player.EntityId == local.EntityId;
            var plateType = FrontlineGcHelper.GetNamePlateColorType(player);
            var gc = FrontlineGcHelper.Resolve(player, ownGc);
            var isAlly = isSelf
                         || FrontlineGcHelper.IsAllyNamePlate(plateType)
                         || (ownGc != FrontlineGc.Unknown && gc == ownGc);

            // 「隐藏本阵营」仅作用于网页推送；游戏内地图已不再叠加我方点
            if (!isSelf && hideOwn && isAlly)
                continue;

            rosterByEntity.TryGetValue(player.EntityId, out var roster);
            var (nx, ny) = WorldToMap01(player.Position, meta);

            var feed = new RadarFeedPlayer
            {
                Id = player.EntityId.ToString("X8"),
                Name = player.Name.TextValue,
                Job = roster?.JobName ?? string.Empty,
                JobAbbr = roster?.JobAbbreviation ?? string.Empty,
                Role = roster?.RoleName ?? string.Empty,
                Gc = GcKey(gc),
                GcName = isAlly && !FrontlineGcHelper.IsHostileGc(gc)
                    ? "我方"
                    : FrontlineGcHelper.ToDisplayName(gc),
                RoleAction = roster?.EquippedRoleActionName ?? string.Empty,
                IsSelf = isSelf,
                IsAlly = isAlly,
                X = nx,
                Y = ny,
                Wx = player.Position.X,
                Wz = player.Position.Z,
                Rot = isSelf ? player.Rotation : 0f,
            };

            if (string.IsNullOrEmpty(feed.Job))
            {
                var jobId = player.ClassJob.RowId;
                feed.Job = RoleHelper.GetChineseJobName(jobId);
                feed.Role = RoleHelper.ToDisplayName(RoleHelper.FromClassJobId(jobId));
            }

            if (isSelf)
                self = feed;
            else
                players.Add(feed);
        }

        var markers = CollectMarkers(meta);
        var bases = ResolveBases(territoryId, meta.Name, ownGc, self, players);

        var state = new RadarFeedState
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            InFrontline = true,
            Active = true,
            TerritoryId = territoryId,
            TerritoryName = FrontlineMapAssets.GetDisplayName(territoryId, meta.Name),
            MapImageUrl = FrontlineMapAssets.GetMapUrl(territoryId, meta.Name),
            Calibration = MapCalibrationDto.From(territoryId, plugin.Config.GetCalibration(territoryId)),
            OwnGc = GcKey(ownGc),
            OwnGcName = FrontlineGcHelper.ToDisplayName(ownGc),
            Self = self,
            Players = players,
            Markers = markers,
            Bases = bases,
            LoadedCount = players.Count + (self == null ? 0 : 1),
            RosterCount = plugin.Roster.Count,
        };

        lock (sync)
            latest = state;
    }

    private void PublishIdle(bool territoryOnly = false)
    {
        basePadGcs = null;
        baseAssignTerritory = 0;
        var territoryId = plugin.ClientState.IsLoggedIn ? plugin.ClientState.TerritoryType : 0u;
        mapByTerritory.TryGetValue(territoryId, out var meta);
        var calTerritory = territoryOnly && territoryId != 0
            ? territoryId
            : FrontlineMapAssets.GloryTerritoryId;
        var state = new RadarFeedState
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            InFrontline = false,
            Active = false,
            TerritoryId = territoryOnly ? territoryId : 0,
            TerritoryName = territoryOnly
                ? FrontlineMapAssets.GetDisplayName(territoryId, meta.Name)
                : string.Empty,
            MapImageUrl = FrontlineMapAssets.GetMapUrl(calTerritory, meta.Name),
            Calibration = MapCalibrationDto.From(calTerritory, plugin.Config.GetCalibration(calTerritory)),
            RosterCount = plugin.Roster.Count,
        };
        lock (sync)
            latest = state;
    }

    private Dictionary<uint, RosterPlayer> BuildRosterLookup()
    {
        var map = new Dictionary<uint, RosterPlayer>();
        foreach (var p in plugin.Roster.Snapshot())
        {
            if (p.EntityId != 0)
                map[p.EntityId] = p;
        }

        return map;
    }

    private List<RadarFeedMarker> CollectMarkers(MapMeta meta)
    {
        FieldMarkerHelper.CollectActive(markerScratch);
        if (markerScratch.Count == 0)
            return [];

        var list = new List<RadarFeedMarker>(markerScratch.Count);
        foreach (var (index, label, pos) in markerScratch)
        {
            var (nx, ny) = WorldToMap01(pos, meta);
            list.Add(new RadarFeedMarker
            {
                Index = index,
                Label = label,
                X = nx,
                Y = ny,
            });
        }

        return list;
    }

    /// <summary>
    /// 将三个出生台匹配到阵营：自己最近的台 → 己方；其余按该阵营玩家靠近程度贪心分配。
    /// </summary>
    private List<RadarFeedBase> ResolveBases(
        uint territoryId,
        string? placeName,
        FrontlineGc ownGc,
        RadarFeedPlayer? self,
        List<RadarFeedPlayer> players)
    {
        var pads = FrontlineSpawnPads.ForTerritory(territoryId, placeName);
        if (pads is not { Length: 3 })
            return [];

        if (territoryId != baseAssignTerritory)
        {
            baseAssignTerritory = territoryId;
            basePadGcs = null;
        }

        var assigned = basePadGcs ?? [FrontlineGc.Unknown, FrontlineGc.Unknown, FrontlineGc.Unknown];

        if (self != null && ownGc != FrontlineGc.Unknown)
        {
            var nearest = NearestPadIndex(pads, self.X, self.Y);
            for (var i = 0; i < 3; i++)
            {
                if (assigned[i] == ownGc && i != nearest)
                    assigned[i] = FrontlineGc.Unknown;
            }

            assigned[nearest] = ownGc;
        }

        var allGcs = new[] { FrontlineGc.Maelstrom, FrontlineGc.TwinAdder, FrontlineGc.Flames };
        var usedGc = new HashSet<FrontlineGc>();
        for (var i = 0; i < 3; i++)
        {
            if (assigned[i] != FrontlineGc.Unknown)
                usedGc.Add(assigned[i]);
        }

        var freePads = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            if (assigned[i] == FrontlineGc.Unknown)
                freePads.Add(i);
        }

        var freeGcs = allGcs.Where(g => !usedGc.Contains(g)).ToList();
        if (freePads.Count > 0 && freeGcs.Count > 0)
        {
            var samples = new List<(FrontlineGc Gc, float X, float Y)>();
            if (self != null && ownGc != FrontlineGc.Unknown)
                samples.Add((ownGc, self.X, self.Y));
            foreach (var p in players)
            {
                var g = ParseGcKey(p.Gc);
                if (g != FrontlineGc.Unknown)
                    samples.Add((g, p.X, p.Y));
            }

            while (freePads.Count > 0 && freeGcs.Count > 0)
            {
                var bestScore = float.NegativeInfinity;
                var bestPad = freePads[0];
                var bestGc = freeGcs[0];
                foreach (var padIdx in freePads)
                {
                    foreach (var gc in freeGcs)
                    {
                        var score = ScoreGcNearPad(samples, gc, pads[padIdx]);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPad = padIdx;
                            bestGc = gc;
                        }
                    }
                }

                // 没有观测样本时按固定台序兜底，避免空白
                if (bestScore <= 0f && samples.Count == 0)
                {
                    assigned[freePads[0]] = freeGcs[0];
                    freeGcs.RemoveAt(0);
                    freePads.RemoveAt(0);
                    continue;
                }

                assigned[bestPad] = bestGc;
                freePads.Remove(bestPad);
                freeGcs.Remove(bestGc);
            }
        }

        basePadGcs = assigned;

        var list = new List<RadarFeedBase>(3);
        for (var i = 0; i < 3; i++)
        {
            var gc = assigned[i];
            if (gc == FrontlineGc.Unknown)
                continue;
            list.Add(new RadarFeedBase
            {
                Gc = GcKey(gc),
                GcName = FrontlineGcHelper.ToDisplayName(gc),
                IsOwn = ownGc != FrontlineGc.Unknown && gc == ownGc,
                X = pads[i].X,
                Y = pads[i].Y,
            });
        }

        return list;
    }

    private static int NearestPadIndex((float X, float Y)[] pads, float x, float y)
    {
        var best = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i < pads.Length; i++)
        {
            var dx = pads[i].X - x;
            var dy = pads[i].Y - y;
            var d = dx * dx + dy * dy;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }

    private static float ScoreGcNearPad(
        List<(FrontlineGc Gc, float X, float Y)> samples,
        FrontlineGc gc,
        (float X, float Y) pad)
    {
        var score = 0f;
        foreach (var s in samples)
        {
            if (s.Gc != gc)
                continue;
            var dx = s.X - pad.X;
            var dy = s.Y - pad.Y;
            var d2 = dx * dx + dy * dy;
            score += 1f / (0.004f + d2);
        }

        return score;
    }

    private static FrontlineGc ParseGcKey(string key) =>
        key switch
        {
            "maelstrom" => FrontlineGc.Maelstrom,
            "twinAdder" => FrontlineGc.TwinAdder,
            "flames" => FrontlineGc.Flames,
            _ => FrontlineGc.Unknown,
        };

    /// <summary>
    /// 世界坐标 → 地图贴图归一化坐标（0–1）。
    /// 公式与常见 FFXIV 地图换算一致。
    /// </summary>
    private static (float X, float Y) WorldToMap01(Vector3 world, MapMeta meta)
    {
        if (meta.SizeFactor == 0)
            return (0.5f, 0.5f);

        var scale = meta.SizeFactor / 100f;
        var x = (world.X / scale + 1024f - meta.OffsetX) / 2048f;
        var y = (world.Z / scale + 1024f - meta.OffsetY) / 2048f;
        return (x, y);
    }

    private static string GcKey(FrontlineGc gc) =>
        gc switch
        {
            FrontlineGc.Maelstrom => "maelstrom",
            FrontlineGc.TwinAdder => "twinAdder",
            FrontlineGc.Flames => "flames",
            _ => "unknown",
        };

    private static RadarFeedState Clone(RadarFeedState src) =>
        new()
        {
            Ts = src.Ts,
            InFrontline = src.InFrontline,
            Active = src.Active,
            TerritoryId = src.TerritoryId,
            TerritoryName = src.TerritoryName,
            MapImageUrl = src.MapImageUrl,
            Calibration = src.Calibration == null
                ? null
                : new MapCalibrationDto
                {
                    TerritoryId = src.Calibration.TerritoryId,
                    OffsetX = src.Calibration.OffsetX,
                    OffsetY = src.Calibration.OffsetY,
                    ScaleX = src.Calibration.ScaleX,
                    ScaleY = src.Calibration.ScaleY,
                    FlipY = src.Calibration.FlipY,
                },
            OwnGc = src.OwnGc,
            OwnGcName = src.OwnGcName,
            Self = src.Self,
            Players = src.Players.ToList(),
            Markers = src.Markers.Select(m => new RadarFeedMarker
            {
                Index = m.Index,
                Label = m.Label,
                X = m.X,
                Y = m.Y,
            }).ToList(),
            Bases = src.Bases.Select(b => new RadarFeedBase
            {
                Gc = b.Gc,
                GcName = b.GcName,
                IsOwn = b.IsOwn,
                X = b.X,
                Y = b.Y,
            }).ToList(),
            LoadedCount = src.LoadedCount,
            RosterCount = src.RosterCount,
        };
}
