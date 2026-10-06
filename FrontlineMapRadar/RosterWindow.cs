using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FrontlineMapRadar;

internal sealed class RosterWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string filter = string.Empty;
    private int roleFilter; // 0=全部
    private int gcFilter;   // 0=全部

    public RosterWindow(Plugin plugin)
        : base("前线玩家名册###FrontlineMapRadarRoster")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 360),
            MaximumSize = new Vector2(1400, 1000),
        };
        Size = new Vector2(900, 480);
    }

    public void Dispose() { }

    public override void Draw()
    {
        var roster = plugin.Roster;
        var list = roster.Snapshot();

        ImGui.TextUnformatted($"本场已记录: {list.Count} 人（离开前线自动清空）");
        ImGui.SameLine();
        if (ImGui.Button("清空列表"))
            roster.Clear();
        ImGui.SameLine();
        if (ImGui.Button("打开设置"))
            plugin.ToggleConfigUi();

        ImGui.Separator();

        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##filter", "搜索名字/职业", ref filter, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("职能##roleFilter", ref roleFilter, "全部\0坦克\0治疗\0近战\0远敏\0远法\0");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("阵营##gcFilter", ref gcFilter, "全部\0黑涡\0双蛇\0恒辉\0未知\0");

        ImGui.Spacing();
        ImGui.TextDisabled("职能技能：每职能三选一。自己直接读取；他人需观察到施放/咏唱后才会显示（未观测为「未知」）。");
        ImGui.TextDisabled("在「标记」列选择 A/B/C/D/1/2/3/4，仅自己可见（本地标记），跟随玩家每帧更新。");

        if (!ImGui.BeginTable(
                "RosterTable",
                8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("名字", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("服务器", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("阵营", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("职能", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("职能技能", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("标记", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        var filterLower = filter.Trim().ToLowerInvariant();
        foreach (var p in list)
        {
            if (!PassFilter(p, filterLower))
                continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (p.CurrentlyLoaded)
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "在场");
            else
                ImGui.TextDisabled("离开");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(p.HomeWorldName) ? "-" : p.HomeWorldName);

            ImGui.TableNextColumn();
            DrawGc(p.GrandCompany);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(p.JobName) ? "-" : p.JobName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(p.RoleName);

            ImGui.TableNextColumn();
            DrawEquippedRoleAction(p);

            ImGui.TableNextColumn();
            DrawMarkerCombo(p);
        }

        ImGui.EndTable();
    }

    private bool PassFilter(RosterPlayer p, string filterLower)
    {
        if (roleFilter is > 0 and <= 5)
        {
            var want = (JobRole)roleFilter;
            // combo: 全部0 坦克1 治疗2 近战3 远敏4 远法5 — 与 JobRole 枚举不完全对齐
            want = roleFilter switch
            {
                1 => JobRole.Tank,
                2 => JobRole.Healer,
                3 => JobRole.Melee,
                4 => JobRole.PhysicalRanged,
                5 => JobRole.MagicalRanged,
                _ => JobRole.Unknown,
            };
            if (p.Role != want)
                return false;
        }

        if (gcFilter > 0)
        {
            var wantGc = gcFilter switch
            {
                1 => FrontlineGc.Maelstrom,
                2 => FrontlineGc.TwinAdder,
                3 => FrontlineGc.Flames,
                4 => FrontlineGc.Unknown,
                _ => FrontlineGc.Unknown,
            };
            if (p.GrandCompany != wantGc)
                return false;
        }

        if (string.IsNullOrEmpty(filterLower))
            return true;

        return p.Name.Contains(filterLower, StringComparison.OrdinalIgnoreCase)
               || p.JobName.Contains(filterLower, StringComparison.OrdinalIgnoreCase)
               || p.JobAbbreviation.Contains(filterLower, StringComparison.OrdinalIgnoreCase)
               || p.RoleName.Contains(filterLower, StringComparison.OrdinalIgnoreCase)
               || p.HomeWorldName.Contains(filterLower, StringComparison.OrdinalIgnoreCase);
    }

    private static void DrawGc(FrontlineGc gc)
    {
        var (text, color) = gc switch
        {
            FrontlineGc.Maelstrom => ("黑涡", new Vector4(0.95f, 0.35f, 0.35f, 1f)),
            FrontlineGc.TwinAdder => ("双蛇", new Vector4(0.95f, 0.85f, 0.25f, 1f)),
            FrontlineGc.Flames => ("恒辉", new Vector4(0.35f, 0.6f, 1f, 1f)),
            _ => ("未知", new Vector4(0.7f, 0.7f, 0.7f, 1f)),
        };
        ImGui.TextColored(color, text);
    }

    private static void DrawEquippedRoleAction(RosterPlayer player)
    {
        if (string.IsNullOrEmpty(player.EquippedRoleActionName))
        {
            ImGui.TextDisabled("未知");
            return;
        }

        ImGui.TextUnformatted(player.EquippedRoleActionName);
    }

    private void DrawMarkerCombo(RosterPlayer player)
    {
        var current = player.MarkerIndex is { } i ? i + 1 : 0; // 0=无
        var labels = "无\0A\0B\0C\0D\01\02\03\04\0";
        ImGui.SetNextItemWidth(-1);
        ImGui.PushID((int)(player.ContentId & 0x7FFFFFFF));
        if (ImGui.Combo("##marker", ref current, labels))
        {
            int? marker = current == 0 ? null : current - 1;
            plugin.Roster.SetMarker(player.ContentId, marker);
        }

        ImGui.PopID();
    }
}
