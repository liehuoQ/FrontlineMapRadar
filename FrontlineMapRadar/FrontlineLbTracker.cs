using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace FrontlineMapRadar;

public sealed class FocusLbHudState
{
    public bool Visible;
    public string Name = string.Empty;
    public string JobName = string.Empty;
    public float Gauge;
    public float SecondsRemaining;
    public int Rank = 1;
    public bool RankUnknown;
    public bool DeadPaused;
    public bool Boosting;
    public string TimingNote = "开战计时";
}

/// <summary>
/// 焦点目标个人极限槽时间积分估算（他人真实槽客户端不可见）。
/// </summary>
public sealed unsafe class FrontlineLbTracker : IDisposable
{
    private const float MatchLengthSec = 1200f;

    private readonly Plugin plugin;
    private readonly FrontlineScoreReader scores;
    private readonly Dictionary<ulong, EntityState> byContentId = new();
    private readonly List<BoostWindow> boosts = [];
    private readonly object sync = new();

    private uint lastTerritory;
    private FocusLbHudState hud = new();

    public FrontlineLbTracker(Plugin plugin)
    {
        this.plugin = plugin;
        scores = new FrontlineScoreReader(plugin);
        plugin.Framework.Update += OnFrameworkUpdate;
        plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        plugin.Framework.Update -= OnFrameworkUpdate;
        plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    public FocusLbHudState GetHud()
    {
        lock (sync)
            return CloneHud(hud);
    }

    public void OnActionEffect(uint casterEntityId, uint actionId)
    {
        if (!plugin.Config.FocusLbPredictEnabled)
            return;
        if (!FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager))
            return;

        IPlayerCharacter? caster = null;
        foreach (var obj in plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter p && p.EntityId == casterEntityId)
            {
                caster = p;
                break;
            }
        }

        if (caster == null)
            return;

        var now = DateTime.UtcNow;
        var key = KeyOf(caster);
        var gc = FrontlineGcHelper.Resolve(caster, FrontlineGcHelper.InferOwnGc(plugin.ObjectTable));

        lock (sync)
        {
            if (FrontlineLbCatalog.IsLimitBreak(actionId))
            {
                var st = GetOrCreate(key, caster, gc);
                st.Gauge = 0;
                st.TimingNote = "观察到 LB 后重置";
                st.DeadPaused = caster.IsDead;
            }

            if (FrontlineLbCatalog.IsFantasiaAction(actionId))
            {
                boosts.Add(new BoostWindow
                {
                    Kind = BoostKind.Fantasia,
                    OriginEntityId = caster.EntityId,
                    OriginGc = gc,
                    OriginPos = caster.Position,
                    RangeLimited = true,
                    UntilUtc = now.AddSeconds(FrontlineLbCatalog.FantasiaDurationSec),
                    ExtraChargeSec = FrontlineLbCatalog.FantasiaExtraChargeSec,
                    DurationSec = FrontlineLbCatalog.FantasiaDurationSec,
                });
            }

            if (FrontlineLbCatalog.IsDawnDanceAction(actionId))
            {
                boosts.Add(new BoostWindow
                {
                    Kind = BoostKind.Dawn,
                    OriginEntityId = caster.EntityId,
                    OriginGc = gc,
                    OriginPos = caster.Position,
                    RangeLimited = false,
                    CasterOnly = true,
                    UntilUtc = now.AddSeconds(FrontlineLbCatalog.DawnDurationSec),
                    ExtraChargeSec = FrontlineLbCatalog.DawnExtraChargeSec,
                    DurationSec = FrontlineLbCatalog.DawnDurationSec,
                });
            }
        }
    }

    private void OnTerritoryChanged(uint _)
    {
        lock (sync)
        {
            byContentId.Clear();
            boosts.Clear();
            hud = new FocusLbHudState();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!plugin.Config.FocusLbPredictEnabled || !plugin.ClientState.IsLoggedIn)
        {
            lock (sync)
                hud = new FocusLbHudState();
            return;
        }

        if (!FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager))
        {
            lock (sync)
            {
                if (lastTerritory != 0)
                {
                    byContentId.Clear();
                    boosts.Clear();
                    lastTerritory = 0;
                }

                hud = new FocusLbHudState();
            }

            return;
        }

        lastTerritory = plugin.ClientState.TerritoryType;
        var dt = (float)framework.UpdateDelta.TotalSeconds;
        if (dt <= 0f || dt > 0.5f)
            dt = 1f / 60f;

        var elapsed = ReadMatchElapsedSec();
        var matchLive = elapsed is >= 0f and < MatchLengthSec;
        var ownGc = FrontlineGcHelper.InferOwnGc(plugin.ObjectTable);
        var board = scores.Read(ownGc);
        var now = DateTime.UtcNow;
        var focus = plugin.TargetManager.FocusTarget as IPlayerCharacter;

        lock (sync)
        {
            PruneBoosts(now);

            if (matchLive)
            {
                foreach (var obj in plugin.ObjectTable)
                {
                    if (obj is not IPlayerCharacter player)
                        continue;

                    var gc = FrontlineGcHelper.Resolve(player, ownGc);
                    var st = GetOrCreate(KeyOf(player), player, gc);
                    TickPlayer(st, player, gc, board, dt, now);
                }
            }

            hud = BuildHud(focus);
        }
    }

    private void TickPlayer(
        EntityState st,
        IPlayerCharacter player,
        FrontlineGc gc,
        in FrontlineScores board,
        float dt,
        DateTime now)
    {
        var jobId = player.ClassJob.RowId;
        if (jobId != st.JobId)
        {
            st.JobId = jobId;
            st.Gauge = 0;
            st.TimingNote = "换职重置";
        }

        st.Gc = gc;
        st.Name = player.Name.TextValue;
        st.EntityId = player.EntityId;
        st.DeadPaused = player.IsDead || player.CurrentHp <= 0;

        if (st.DeadPaused || !FrontlineLbCatalog.TryGetJob(jobId, out var job))
            return;

        var rank = FrontlineScoreReader.RankFor(board, gc == FrontlineGc.Unknown ? FrontlineGc.Unknown : gc);
        st.Rank = rank;
        st.RankUnknown = !board.Parsed || gc == FrontlineGc.Unknown;

        var chargeSec = FrontlineLbCatalog.EffectiveChargeSec(job);
        var rate = FrontlineLbCatalog.RankRateMultiplier(st.RankUnknown ? 1 : rank);
        st.Gauge = Math.Clamp(st.Gauge + dt * rate / chargeSec, 0f, 1f);

        var boost = ComputeBoostExtra(st, player, now, chargeSec, dt);
        st.Boosting = boost > 0 || HasActiveBoostStatus(player);
        if (boost > 0)
            st.Gauge = Math.Clamp(st.Gauge + boost, 0f, 1f);

        st.SecondsRemaining = st.Gauge >= 1f
            ? 0f
            : (1f - st.Gauge) * chargeSec / Math.Max(0.01f, rate);
    }

    private float ComputeBoostExtra(EntityState st, IPlayerCharacter player, DateTime now, float chargeSec, float dt)
    {
        var extra = 0f;
        var coveredByWindow = false;

        foreach (var b in boosts)
        {
            if (now >= b.UntilUtc)
                continue;

            var applies = false;
            if (b.Kind == BoostKind.Fantasia)
            {
                if (b.OriginGc != FrontlineGc.Unknown && st.Gc == b.OriginGc)
                {
                    applies = !b.RangeLimited
                              || player.EntityId == b.OriginEntityId
                              || Vector3.Distance(player.Position, b.OriginPos) <= FrontlineLbCatalog.FantasiaRangeYalms;
                }

                if (!applies && HasFantasiaStatus(player))
                    applies = true;
            }
            else if (b.Kind == BoostKind.Dawn)
            {
                if (b.CasterOnly && player.EntityId == b.OriginEntityId)
                    applies = true;
                if (HasDawnStatus(player))
                    applies = true;
            }

            if (!applies)
                continue;

            coveredByWindow = true;
            extra += dt * b.ExtraChargeSec / Math.Max(0.1f, b.DurationSec) / chargeSec;
        }

        if (!coveredByWindow)
        {
            if (HasFantasiaStatus(player))
            {
                extra += dt * FrontlineLbCatalog.FantasiaExtraChargeSec
                         / FrontlineLbCatalog.FantasiaDurationSec / chargeSec;
            }
            else if (HasDawnStatus(player))
            {
                extra += dt * FrontlineLbCatalog.DawnExtraChargeSec
                         / FrontlineLbCatalog.DawnDurationSec / chargeSec;
            }
        }

        return extra;
    }

    private void PruneBoosts(DateTime now)
    {
        for (var i = boosts.Count - 1; i >= 0; i--)
        {
            if (now >= boosts[i].UntilUtc)
                boosts.RemoveAt(i);
        }
    }

    private FocusLbHudState BuildHud(IPlayerCharacter? focus)
    {
        if (focus == null)
            return new FocusLbHudState();

        var key = KeyOf(focus);
        if (!byContentId.TryGetValue(key, out var st))
            return new FocusLbHudState();

        var rankUnknown = st.RankUnknown;
        return new FocusLbHudState
        {
            Visible = true,
            Name = st.Name,
            JobName = RoleHelper.GetChineseJobName(st.JobId),
            Gauge = st.Gauge,
            SecondsRemaining = st.SecondsRemaining,
            Rank = st.Rank,
            RankUnknown = rankUnknown,
            DeadPaused = st.DeadPaused,
            Boosting = st.Boosting,
            TimingNote = st.TimingNote,
        };
    }

    private EntityState GetOrCreate(ulong key, IPlayerCharacter player, FrontlineGc gc)
    {
        if (!byContentId.TryGetValue(key, out var st))
        {
            st = new EntityState
            {
                JobId = player.ClassJob.RowId,
                Gauge = 0,
                TimingNote = "开战计时",
                Gc = gc,
                Name = player.Name.TextValue,
                EntityId = player.EntityId,
            };
            byContentId[key] = st;
        }

        return st;
    }

    private static ulong KeyOf(IPlayerCharacter player)
    {
        var character = (Character*)player.Address;
        var contentId = character != null ? character->ContentId : 0;
        return contentId != 0 ? contentId : player.EntityId;
    }

    private static float ReadMatchElapsedSec()
    {
        try
        {
            var fw = EventFramework.Instance();
            if (fw == null)
                return -1f;

            var director = fw->GetInstanceContentDirector();
            if (director == null)
                return -1f;

            var left = director->ContentDirector.ContentTimeLeft;
            if (left <= 0 || left > MatchLengthSec + 5f)
                return -1f;

            return MatchLengthSec - left;
        }
        catch
        {
            return -1f;
        }
    }

    private static bool HasActiveBoostStatus(IBattleChara player)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId == 0)
                continue;
            if (FrontlineLbCatalog.IsAnyBoostStatus(s.StatusId))
                return true;
        }

        return false;
    }

    private static bool HasDawnStatus(IBattleChara player)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId != 0 && FrontlineLbCatalog.IsDawnBoostStatus(s.StatusId))
                return true;
        }

        return false;
    }

    private static bool HasFantasiaStatus(IBattleChara player)
    {
        foreach (var s in player.StatusList)
        {
            if (s.StatusId != 0 && FrontlineLbCatalog.IsFantasiaStatus(s.StatusId))
                return true;
        }

        return false;
    }

    private static FocusLbHudState CloneHud(FocusLbHudState s) =>
        new()
        {
            Visible = s.Visible,
            Name = s.Name,
            JobName = s.JobName,
            Gauge = s.Gauge,
            SecondsRemaining = s.SecondsRemaining,
            Rank = s.Rank,
            RankUnknown = s.RankUnknown,
            DeadPaused = s.DeadPaused,
            Boosting = s.Boosting,
            TimingNote = s.TimingNote,
        };

    private sealed class EntityState
    {
        public uint EntityId;
        public uint JobId;
        public float Gauge;
        public float SecondsRemaining;
        public int Rank = 1;
        public bool RankUnknown;
        public bool DeadPaused;
        public bool Boosting;
        public string TimingNote = "开战计时";
        public FrontlineGc Gc;
        public string Name = string.Empty;
    }

    private enum BoostKind
    {
        Fantasia,
        Dawn,
    }

    private struct BoostWindow
    {
        public BoostKind Kind;
        public uint OriginEntityId;
        public FrontlineGc OriginGc;
        public Vector3 OriginPos;
        public bool RangeLimited;
        public bool CasterOnly;
        public DateTime UntilUtc;
        public float ExtraChargeSec;
        public float DurationSec;
    }
}
