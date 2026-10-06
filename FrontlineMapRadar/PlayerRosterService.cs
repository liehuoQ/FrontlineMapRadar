using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 扫描并记录本场纷争前线出现过的玩家，并维护本地标记跟随。
/// </summary>
public sealed class PlayerRosterService : IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<ulong, RosterPlayer> players = new();
    private readonly Dictionary<uint, ulong> entityToContentId = new();
    private readonly object sync = new();
    private PvpRoleActionScanner? roleActionScanner;

    private bool wasInFrontline;

    public PlayerRosterService(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        plugin.Framework.Update += OnFrameworkUpdate;
        roleActionScanner = new PvpRoleActionScanner(plugin, this);
    }

    public void Dispose()
    {
        plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        plugin.Framework.Update -= OnFrameworkUpdate;
        roleActionScanner?.Dispose();
        roleActionScanner = null;
        ClearMarkers();
        lock (sync)
        {
            players.Clear();
            entityToContentId.Clear();
        }
    }

    /// <summary>根据 EntityId 记录观测到的职能技能（施放 / 咏唱 / 本地 DutyAction）。</summary>
    public void RecordEquippedRoleAction(uint entityId, uint actionId)
    {
        if (entityId == 0 || !PvpRoleActionCatalog.TryGet(actionId, out var entry))
            return;

        lock (sync)
        {
            if (!entityToContentId.TryGetValue(entityId, out var contentId))
            {
                // 可能尚未扫入名册：先按 EntityId 在表里找
                foreach (var p in players.Values)
                {
                    if (p.EntityId == entityId)
                    {
                        contentId = p.ContentId;
                        entityToContentId[entityId] = contentId;
                        break;
                    }
                }
            }

            if (contentId == 0 || !players.TryGetValue(contentId, out var player))
                return;

            if (player.EquippedRoleActionId == actionId)
                return;

            player.EquippedRoleActionId = actionId;
            player.EquippedRoleActionName = entry.Name;
        }
    }

    public IReadOnlyList<RosterPlayer> Snapshot()
    {
        lock (sync)
            return players.Values
                .OrderBy(p => p.GrandCompany)
                .ThenBy(p => p.Role)
                .ThenBy(p => p.JobAbbreviation, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();
    }

    public int Count
    {
        get
        {
            lock (sync)
                return players.Count;
        }
    }

    public void Clear()
    {
        ClearMarkers();
        lock (sync)
        {
            players.Clear();
            entityToContentId.Clear();
        }
    }

    public void SetMarker(ulong contentId, int? markerIndex)
    {
        lock (sync)
        {
            if (!players.TryGetValue(contentId, out var player))
                return;

            // 同一标记只能绑一个人
            if (markerIndex is { } idx)
            {
                foreach (var other in players.Values)
                {
                    if (other.ContentId != contentId && other.MarkerIndex == idx)
                        other.MarkerIndex = null;
                }
            }

            player.MarkerIndex = markerIndex;
        }
    }

    private void OnTerritoryChanged(uint territory)
    {
        wasInFrontline = false;
        Clear();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!plugin.ClientState.IsLoggedIn)
            return;

        var inFrontline = FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager);
        if (!inFrontline)
        {
            if (wasInFrontline)
                Clear();
            wasInFrontline = false;
            return;
        }

        wasInFrontline = true;
        ScanPlayers();
        roleActionScanner?.Tick();
    }

    private unsafe void ScanPlayers()
    {
        var ownGc = FrontlineGcHelper.InferOwnGc(plugin.ObjectTable);
        var sheet = plugin.DataManager.GetExcelSheet<ClassJob>();
        var worldSheet = plugin.DataManager.GetExcelSheet<World>();

        lock (sync)
        {
            foreach (var p in players.Values)
            {
                p.SeenThisScan = false;
                p.CurrentlyLoaded = false;
            }

            foreach (var obj in plugin.ObjectTable)
            {
                if (obj is not IPlayerCharacter player)
                    continue;
                if (player.ObjectKind != ObjectKind.Pc)
                    continue;

                var character = (Character*)player.Address;
                if (character == null)
                    continue;

                var contentId = character->ContentId;
                if (contentId == 0)
                    continue;

                var jobId = player.ClassJob.RowId;
                var sheetJobName = string.Empty;
                var jobAbbr = string.Empty;
                byte luminaRole = 0;
                if (sheet != null && sheet.TryGetRow(jobId, out var job))
                {
                    sheetJobName = job.Name.ToString();
                    jobAbbr = job.Abbreviation.ToString();
                    luminaRole = job.Role;
                }

                var jobName = RoleHelper.GetChineseJobName(jobId, sheetJobName);

                var worldId = player.HomeWorld.RowId;
                var worldName = string.Empty;
                if (worldSheet != null && worldSheet.TryGetRow(worldId, out var world))
                    worldName = world.Name.ToString();

                var gc = FrontlineGcHelper.Resolve(player, ownGc);
                var role = RoleHelper.FromClassJob(jobId, luminaRole);

                if (!players.TryGetValue(contentId, out var entry))
                {
                    entry = new RosterPlayer { ContentId = contentId };
                    players[contentId] = entry;
                }

                entry.Name = player.Name.TextValue;
                entry.HomeWorldId = worldId;
                entry.HomeWorldName = worldName;
                entry.ClassJobId = jobId;
                entry.JobName = jobName;
                entry.JobAbbreviation = jobAbbr;
                entry.Role = role;
                if (gc != FrontlineGc.Unknown)
                    entry.GrandCompany = gc;
                entry.LastPosition = player.Position;
                entry.EntityId = player.EntityId;
                entry.SeenThisScan = true;
                entry.CurrentlyLoaded = true;
                entityToContentId[player.EntityId] = contentId;
            }
        }
    }

    /// <summary>本地点名册标记（仅本地绘制，每帧位置来自玩家坐标）。</summary>
    public IReadOnlyList<(int Index, string Label, Vector3 Position, uint EntityId)> SnapshotLocalMarkers()
    {
        lock (sync)
        {
            return players.Values
                .Where(p => p.MarkerIndex is not null && p.CurrentlyLoaded)
                .Select(p =>
                {
                    var idx = p.MarkerIndex!.Value;
                    var label = idx >= 0 && idx < RosterPlayer.FieldMarkerLabels.Length
                        ? RosterPlayer.FieldMarkerLabels[idx]
                        : (idx + 1).ToString();
                    return (idx, label, p.LastPosition, p.EntityId);
                })
                .ToList();
        }
    }

    private void ClearMarkers()
    {
        lock (sync)
        {
            foreach (var p in players.Values)
                p.MarkerIndex = null;
        }
    }
}
