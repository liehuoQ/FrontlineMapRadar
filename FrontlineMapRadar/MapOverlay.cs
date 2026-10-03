using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;
using Lumina.Excel.Sheets;

namespace FrontlineMapRadar;

/// <summary>
/// 在原生 AreaMap 上叠加绘制玩家点。坐标换算参考开源 Radar 插件的做法。
/// </summary>
internal sealed unsafe class MapOverlay : IDisposable
{
    private readonly Plugin plugin;

    private Vector2? mapOrigin;
    private Vector2 mapMin;
    private Vector2 mapMax;
    private float globalUiScale = 1f;
    private float worldToMapScale = 1f;
    private Vector3 myWorldPos;
    private readonly System.Collections.Generic.Dictionary<uint, ushort> sizeFactorByTerritory = new();

    // AreaMap 控件内「当前缩放」字段偏移（与 KirisameVanilla/Radar 一致，版本变更时可能需调整）
    private const int AreaMapZoomOffset = 980;

    public MapOverlay(Plugin plugin)
    {
        this.plugin = plugin;

        var sheet = plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                if (row.Map.IsValid)
                    sizeFactorByTerritory[row.RowId] = row.Map.Value.SizeFactor;
            }
        }

        plugin.PluginInterface.UiBuilder.Draw += OnDraw;
    }

    public void Dispose()
    {
        plugin.PluginInterface.UiBuilder.Draw -= OnDraw;
    }

    private void OnDraw()
    {
        if (!plugin.Config.Enabled)
            return;

        if (!FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager))
            return;

        if (plugin.ObjectTable.LocalPlayer is not { } localPlayer)
            return;

        if (!TryRefreshMapGeometry(localPlayer.Position))
            return;

        var ownGc = FrontlineGcHelper.InferOwnGc(plugin.ObjectTable);
        var cfg = plugin.Config;
        var maelstrom = ImGui.ColorConvertFloat4ToU32(cfg.MaelstromColor);
        var twinAdder = ImGui.ColorConvertFloat4ToU32(cfg.TwinAdderColor);
        var flames = ImGui.ColorConvertFloat4ToU32(cfg.FlamesColor);
        var unknown = ImGui.ColorConvertFloat4ToU32(cfg.UnknownColor);
        var outlineColor = ImGui.ColorConvertFloat4ToU32(cfg.OutlineColor);
        var radius = Math.Clamp(cfg.DotRadius, 1f, 20f) * globalUiScale;
        var outline = Math.Max(0f, cfg.OutlineThickness);

        var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        drawList.PushClipRect(mapMin, mapMax);

        foreach (var obj in plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            if (player.EntityId == localPlayer.EntityId)
                continue;

            if (player.ObjectKind != ObjectKind.Pc)
                continue;

            var gc = FrontlineGcHelper.Resolve(player, ownGc);
            if (cfg.HideOwnTeam && ownGc != FrontlineGc.Unknown && gc == ownGc)
                continue;

            var color = gc switch
            {
                FrontlineGc.Maelstrom => maelstrom,
                FrontlineGc.TwinAdder => twinAdder,
                FrontlineGc.Flames => flames,
                _ => unknown,
            };

            var screen = WorldToMap(mapOrigin!.Value, player.Position);
            if (outline > 0f)
                drawList.AddCircleFilled(screen, radius + outline, outlineColor, 16);
            drawList.AddCircleFilled(screen, radius, color, 16);
        }

        drawList.PopClipRect();
    }

    private Vector2 WorldToMap(Vector2 origin, Vector3 world)
    {
        var delta = new Vector2(world.X - myWorldPos.X, world.Z - myWorldPos.Z) * worldToMapScale;
        return origin + delta;
    }

    private bool TryRefreshMapGeometry(Vector3 localPos)
    {
        mapOrigin = null;
        myWorldPos = localPos;

        var addonPtr = plugin.GameGui.GetAddonByName("AreaMap");
        if (addonPtr.IsNull)
            return false;

        var areaMap = (AtkUnitBase*)addonPtr.Address;
        if (areaMap == null || !areaMap->IsVisible)
            return false;

        globalUiScale = areaMap->Scale;
        if (areaMap->UldManager.NodeListCount <= 4)
            return false;

        var mapRoot = (AtkComponentNode*)areaMap->UldManager.NodeList[3];
        if (mapRoot == null)
            return false;

        var mapRootNode = mapRoot->AtkResNode;
        if (mapRoot->Component->UldManager.NodeListCount < 233)
            return false;

        for (var i = 6; i < mapRoot->Component->UldManager.NodeListCount - 1; i++)
        {
            var node = mapRoot->Component->UldManager.NodeList[i];
            if (node == null || !node->IsVisible())
                continue;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component->UldManager.NodeListCount <= 4)
                continue;

            var imageNode = (AtkImageNode*)componentNode->Component->UldManager.NodeList[4];
            if (imageNode == null || imageNode->PartsList == null)
                continue;

            if (imageNode->PartId > imageNode->PartsList->PartCount)
                continue;

            var uldAsset = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
            if (uldAsset == null || uldAsset->AtkTexture.TextureType != TextureType.Resource)
                continue;

            StdString fileName = uldAsset->AtkTexture.Resource->TexFileResourceHandle->ResourceHandle.FileName;
            var tex = Path.GetFileName(fileName.ToString());
            if (tex is not ("060443.tex" or "060443_hr1.tex"))
                continue;

            var markerNode = componentNode->AtkResNode;
            var addonPos = new Vector2(areaMap->X, areaMap->Y);
            var viewport = ImGui.GetMainViewport().Pos;

            mapOrigin = viewport + addonPos
                        + (new Vector2(mapRootNode.X, mapRootNode.Y)
                           + new Vector2(markerNode.X, markerNode.Y)
                           + new Vector2(markerNode.OriginX, markerNode.OriginY)) * globalUiScale;

            mapMin = viewport + addonPos + new Vector2(mapRootNode.X, mapRootNode.Y) * globalUiScale;
            mapMax = viewport + addonPos + new Vector2(mapRootNode.X, mapRootNode.Y)
                     + new Vector2(mapRootNode.Width, mapRootNode.Height) * globalUiScale;

            var zoom = ReadMapZoom(areaMap);
            var sizeFactor = 100f;
            if (sizeFactorByTerritory.TryGetValue(plugin.ClientState.TerritoryType, out var sf))
                sizeFactor = sf;

            worldToMapScale = zoom * sizeFactor / 100f * globalUiScale;
            return mapOrigin.HasValue;
        }

        return false;
    }

    private static float ReadMapZoom(AtkUnitBase* areaMap)
    {
        try
        {
            return *(float*)((byte*)areaMap + AreaMapZoomOffset);
        }
        catch
        {
            return 1f;
        }
    }
}
