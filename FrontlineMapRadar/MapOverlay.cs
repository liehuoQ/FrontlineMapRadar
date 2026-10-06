using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    private readonly System.Collections.Generic.Dictionary<uint, MapScale> mapScaleByTerritory = new();

    private readonly record struct MapScale(ushort SizeFactor, short OffsetX, short OffsetY);

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
                {
                    var map = row.Map.Value;
                    mapScaleByTerritory[row.RowId] = new MapScale(map.SizeFactor, map.OffsetX, map.OffsetY);
                }
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

        var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        DrawWorldLocalMarkers(drawList);

        if (!TryRefreshMapGeometry(localPlayer.Position))
            return;

        var cfg = plugin.Config;
        var maelstrom = ImGui.ColorConvertFloat4ToU32(cfg.MaelstromColor);
        var twinAdder = ImGui.ColorConvertFloat4ToU32(cfg.TwinAdderColor);
        var flames = ImGui.ColorConvertFloat4ToU32(cfg.FlamesColor);
        var outlineColor = ImGui.ColorConvertFloat4ToU32(cfg.OutlineColor);
        var radius = Math.Clamp(cfg.DotRadius, 1f, 20f) * globalUiScale;
        var outline = Math.Max(0f, cfg.OutlineThickness);

        drawList.PushClipRect(mapMin, mapMax);

        foreach (var obj in plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player)
                continue;

            if (player.EntityId == localPlayer.EntityId)
                continue;

            if (player.ObjectKind != ObjectKind.Pc)
                continue;

            // 只叠加敌方三大国色；我方交给游戏自带地图标记，不再画灰色「未知」点
            var gc = FrontlineGcHelper.FromNamePlateColorType(
                FrontlineGcHelper.GetNamePlateColorType(player));
            if (!FrontlineGcHelper.IsHostileGc(gc))
                continue;

            uint color;
            switch (gc)
            {
                case FrontlineGc.Maelstrom:
                    color = maelstrom;
                    break;
                case FrontlineGc.TwinAdder:
                    color = twinAdder;
                    break;
                case FrontlineGc.Flames:
                    color = flames;
                    break;
                default:
                    continue;
            }

            var screen = WorldToMap(mapOrigin!.Value, player.Position);
            if (outline > 0f)
                drawList.AddCircleFilled(screen, radius + outline, outlineColor, 16);
            drawList.AddCircleFilled(screen, radius, color, 16);
        }

        DrawMapLocalMarkers(drawList);
        drawList.PopClipRect();
    }

    private static readonly uint[] LocalMarkerColors =
    [
        0xFF6B6BFF, // A 红
        0xFF4CD4F0, // B 黄
        0xFFFFA75A, // C 蓝
        0xFFFF7CD0, // D 紫
        0xFF658AFF, // 1
        0xFF82E0FF, // 2
        0xFFF6B564, // 3
        0xFFD893CE, // 4
    ];

    private void DrawWorldLocalMarkers(ImDrawListPtr drawList)
    {
        foreach (var (index, label, pos, _) in plugin.Roster.SnapshotLocalMarkers())
        {
            if (!plugin.GameGui.WorldToScreen(pos + new Vector3(0, 1.2f, 0), out var screen))
                continue;

            var color = LocalMarkerColors[index & 7];
            DrawLocalMarkerGlyph(drawList, screen, 11f * globalUiScale, label, color);
        }
    }

    private void DrawMapLocalMarkers(ImDrawListPtr drawList)
    {
        if (!mapOrigin.HasValue)
            return;

        var size = Math.Max(10f, 12f * globalUiScale);
        foreach (var (index, label, pos, _) in plugin.Roster.SnapshotLocalMarkers())
        {
            var screen = WorldToMap(mapOrigin.Value, pos);
            var color = LocalMarkerColors[index & 7];
            DrawLocalMarkerGlyph(drawList, screen, size, label, color);
        }
    }

    private static void DrawLocalMarkerGlyph(ImDrawListPtr drawList, Vector2 center, float r, string label, uint color)
    {
        var outline = 0xE0101218;
        var p0 = center + new Vector2(0, -r);
        var p1 = center + new Vector2(r * 0.82f, 0);
        var p2 = center + new Vector2(0, r);
        var p3 = center + new Vector2(-r * 0.82f, 0);
        drawList.AddTriangleFilled(p0, p1, center, outline);
        drawList.AddTriangleFilled(p1, p2, center, outline);
        drawList.AddTriangleFilled(p2, p3, center, outline);
        drawList.AddTriangleFilled(p3, p0, center, outline);
        drawList.AddQuad(p0, p1, p2, p3, color, 2f);

        var textSize = ImGui.CalcTextSize(label);
        var textPos = center - textSize * 0.5f;
        drawList.AddText(textPos + new Vector2(1, 1), 0xE0101218, label);
        drawList.AddText(textPos, 0xFFFFFFFF, label);
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

        var rootPtr = areaMap->UldManager.NodeList[3];
        if (rootPtr == null)
            return false;

        var mapRoot = (AtkComponentNode*)rootPtr;
        var mapRootNode = mapRoot->AtkResNode;
        var addonPos = new Vector2(areaMap->X, areaMap->Y);
        var viewport = ImGui.GetMainViewport().Pos;
        mapMin = viewport + addonPos + new Vector2(mapRootNode.X, mapRootNode.Y) * globalUiScale;
        mapMax = viewport + addonPos + new Vector2(mapRootNode.X, mapRootNode.Y)
                 + new Vector2(mapRootNode.Width, mapRootNode.Height) * globalUiScale;

        var sizeFactorFloat = ReadSizeFactorFloat();
        var mapScale = ReadAreaMapScale(areaMap);
        worldToMapScale = mapScale * sizeFactorFloat * globalUiScale;

        Vector2? origin = null;
        if (mapRoot->Component != null)
            origin = TryYouAreHereScreenPos(mapRoot, mapRootNode, addonPos, viewport);
        origin ??= TryPlayerPinScreenPos((AddonAreaMap*)areaMap, mapRootNode, addonPos, viewport);
        origin ??= TryPlayerMarkerScreenPos((AddonAreaMap*)areaMap);

        if (origin == null)
            return false;

        mapOrigin = origin;
        return true;
    }

    private float ReadSizeFactorFloat()
    {
        var agent = AgentMap.Instance();
        if (agent != null && agent->CurrentMapSizeFactorFloat > 0.01f)
            return agent->CurrentMapSizeFactorFloat;

        if (mapScaleByTerritory.TryGetValue(plugin.ClientState.TerritoryType, out var sheet)
            && sheet.SizeFactor > 0)
            return sheet.SizeFactor / 100f;

        return 1f;
    }

    private static float ReadAreaMapScale(AtkUnitBase* areaMap)
    {
        var addon = (AddonAreaMap*)areaMap;
        var scale = addon->AreaMap.MapScale;
        if (scale > 0.01f && !float.IsNaN(scale))
            return scale;

        try
        {
            var legacy = *(float*)((byte*)areaMap + AreaMapZoomOffset);
            if (legacy > 0.01f && !float.IsNaN(legacy))
                return legacy;
        }
        catch
        {
            // ignore
        }

        return 1f;
    }

    private Vector2? TryPlayerPinScreenPos(
        AddonAreaMap* addon,
        AtkResNode mapRootNode,
        Vector2 addonPos,
        Vector2 viewport)
    {
        var pin = addon->AreaMap.PlayerPin;
        if (pin == null)
            return null;

        var markerNode = pin->AtkResNode;
        if (!markerNode.IsVisible())
            return null;

        return viewport + addonPos
               + (new Vector2(mapRootNode.X, mapRootNode.Y)
                  + new Vector2(markerNode.X, markerNode.Y)
                  + new Vector2(markerNode.OriginX, markerNode.OriginY)) * globalUiScale;
    }

    private Vector2? TryPlayerMarkerScreenPos(AddonAreaMap* addon)
    {
        var x = addon->AreaMap.PlayerMarkerX;
        var y = addon->AreaMap.PlayerMarkerY;
        if (float.IsNaN(x) || float.IsNaN(y))
            return null;

        // PlayerMarker 以地图中心为 0；与「你在这里」钉对齐
        var center = (mapMin + mapMax) * 0.5f;
        return center + new Vector2(x, y) * globalUiScale;
    }

    private Vector2? TryYouAreHereScreenPos(
        AtkComponentNode* mapRoot,
        AtkResNode mapRootNode,
        Vector2 addonPos,
        Vector2 viewport)
    {
        var count = mapRoot->Component->UldManager.NodeListCount;
        // 原 Radar 从 6 起扫；尘封秘岩节点更少，找不到再扫全部（不再要求 ≥233）
        if (TryFindYouAreHere(mapRoot, 6, count, mapRootNode, addonPos, viewport, out var pos))
            return pos;
        if (TryFindYouAreHere(mapRoot, 0, count, mapRootNode, addonPos, viewport, out pos))
            return pos;
        return null;
    }

    private bool TryFindYouAreHere(
        AtkComponentNode* mapRoot,
        int start,
        int count,
        AtkResNode mapRootNode,
        Vector2 addonPos,
        Vector2 viewport,
        out Vector2 pos)
    {
        pos = default;
        for (var i = start; i < count; i++)
        {
            var node = mapRoot->Component->UldManager.NodeList[i];
            if (node == null || !node->IsVisible())
                continue;
            if (node->Type != NodeType.Component)
                continue;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component == null)
                continue;

            if (!IsYouAreHereTexture(componentNode))
                continue;

            var markerNode = componentNode->AtkResNode;
            pos = viewport + addonPos
                  + (new Vector2(mapRootNode.X, mapRootNode.Y)
                     + new Vector2(markerNode.X, markerNode.Y)
                     + new Vector2(markerNode.OriginX, markerNode.OriginY)) * globalUiScale;
            return true;
        }

        return false;
    }

    private static bool IsYouAreHereTexture(AtkComponentNode* componentNode)
    {
        var inner = componentNode->Component->UldManager.NodeListCount;
        for (var j = 0; j < inner; j++)
        {
            var child = componentNode->Component->UldManager.NodeList[j];
            if (child == null || child->Type != NodeType.Image)
                continue;

            var imageNode = (AtkImageNode*)child;
            if (imageNode->PartsList == null)
                continue;
            if (imageNode->PartId > imageNode->PartsList->PartCount)
                continue;

            var uldAsset = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
            if (uldAsset == null || uldAsset->AtkTexture.TextureType != TextureType.Resource)
                continue;
            if (uldAsset->AtkTexture.Resource == null
                || uldAsset->AtkTexture.Resource->TexFileResourceHandle == null)
                continue;

            StdString fileName = uldAsset->AtkTexture.Resource->TexFileResourceHandle->ResourceHandle.FileName;
            var tex = Path.GetFileName(fileName.ToString());
            if (tex is "060443.tex" or "060443_hr1.tex")
                return true;
        }

        return false;
    }
}
