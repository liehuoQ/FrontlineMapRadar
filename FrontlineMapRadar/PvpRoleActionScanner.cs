using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace FrontlineMapRadar;

/// <summary>
/// 探测玩家当前携带的 PvP 职能技能（每职能三选一）。
/// - 自己：DutyAction 槽位 + PvP Profile 选中索引
/// - 他人：无法直接读配置；通过咏唱 / ActionEffect 施放记录
/// </summary>
internal sealed unsafe class PvpRoleActionScanner : IDisposable
{
    private readonly Plugin plugin;
    private readonly PlayerRosterService roster;
    private Hook<ActionEffectHandler.Delegates.Receive>? receiveHook;

    public PvpRoleActionScanner(Plugin plugin, PlayerRosterService roster)
    {
        this.plugin = plugin;
        this.roster = roster;

        try
        {
            receiveHook = plugin.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.MemberFunctionPointers.Receive,
                ReceiveDetour);
            receiveHook.Enable();
            plugin.Log.Information("PvP role-action ActionEffect hook enabled.");
        }
        catch (Exception ex)
        {
            plugin.Log.Error(ex, "Failed to hook ActionEffectHandler.Receive; other players' role actions only via cast bar.");
        }
    }

    public void Dispose()
    {
        receiveHook?.Disable();
        receiveHook?.Dispose();
        receiveHook = null;
    }

    /// <summary>每帧：读本地 DutyAction / PvP Profile，并扫描他人咏唱栏。</summary>
    public void Tick()
    {
        ScanLocalPlayer();
        ScanCastBars();
    }

    private void ScanLocalPlayer()
    {
        var local = plugin.ObjectTable.LocalPlayer;
        if (local == null)
            return;

        // Duty Action 槽（前线中职能技能常落在此）
        for (ushort slot = 0; slot < 5; slot++)
        {
            var actionId = DutyActionManager.GetDutyActionId(slot);
            if (actionId != 0 && PvpRoleActionCatalog.IsRoleAction(actionId))
            {
                roster.RecordEquippedRoleAction(local.EntityId, actionId);
                return;
            }
        }

        var duty = DutyActionManager.GetInstanceIfReady();
        if (duty != null)
        {
            for (var i = 0; i < duty->NumValidSlots && i < 5; i++)
            {
                var actionId = duty->ActionId[i];
                if (actionId != 0 && PvpRoleActionCatalog.IsRoleAction(actionId))
                {
                    roster.RecordEquippedRoleAction(local.EntityId, actionId);
                    return;
                }
            }
        }

        // PvP Profile：SelectedRoleActionIndex 对应职能三选一的下标
        var agent = AgentPvpProfile.Instance();
        if (agent == null)
            return;

        var role = RoleHelper.FromClassJobId(local.ClassJob.RowId);
        var options = PvpRoleActionCatalog.ForRole(role);
        var idx = agent->SelectedRoleActionIndex;
        if (options.Length > 0 && idx < options.Length)
            roster.RecordEquippedRoleAction(local.EntityId, options[idx].ActionId);
    }

    private void ScanCastBars()
    {
        foreach (var obj in plugin.ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                continue;

            var character = (Character*)obj.Address;
            if (character == null)
                continue;

            var cast = character->GetCastInfo();
            if (cast == null || !cast->IsCasting)
                continue;

            var actionId = cast->ActionId;
            if (PvpRoleActionCatalog.IsRoleAction(actionId))
                roster.RecordEquippedRoleAction(obj.EntityId, actionId);
        }
    }

    private void ReceiveDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        try
        {
            if (header != null)
            {
                var actionId = header->ActionId;
                if (PvpRoleActionCatalog.IsRoleAction(actionId))
                    roster.RecordEquippedRoleAction(casterEntityId, actionId);

                plugin.LbTracker.OnActionEffect(casterEntityId, actionId);
            }
        }
        catch (Exception ex)
        {
            plugin.Log.Error(ex, "Error in ActionEffect receive detour");
        }

        receiveHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
    }
}
