using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FrontlineMapRadar;

/// <summary>
/// 游戏内焦点目标极限技积蓄预测 HUD（无边框、不挡点击）。
/// </summary>
internal sealed class FocusLbHud : IDisposable
{
    private readonly Plugin plugin;

    public FocusLbHud(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.PluginInterface.UiBuilder.Draw += OnDraw;
    }

    public void Dispose()
    {
        plugin.PluginInterface.UiBuilder.Draw -= OnDraw;
    }

    private void OnDraw()
    {
        if (!plugin.Config.FocusLbPredictEnabled)
            return;

        if (!FrontlineHelper.IsInFrontline(plugin.ClientState, plugin.DataManager))
            return;

        var state = plugin.LbTracker.GetHud();
        if (!state.Visible)
            return;

        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoInputs
                    | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoNav;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X * 0.5f, 72f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.72f);

        if (!ImGui.Begin("###FrontlineFocusLbHud", flags))
        {
            ImGui.End();
            return;
        }

        var pct = Math.Clamp(state.Gauge, 0f, 1f);
        ImGui.TextUnformatted($"{state.Name} · {state.JobName}");
        ImGui.TextUnformatted($"极限技预测  {pct * 100f:0}%");
        ImGui.ProgressBar(pct, new Vector2(220f, 10f), string.Empty);

        if (state.DeadPaused)
            ImGui.TextDisabled("死后暂停积蓄");
        else if (pct >= 1f)
            ImGui.TextUnformatted("预计已充满");
        else
            ImGui.TextUnformatted($"预计充满剩余 {state.SecondsRemaining:0} 秒");

        var rankText = state.RankUnknown
            ? "名次未知（按第一名保守估计）"
            : $"使用名次：第 {state.Rank} 名";
        ImGui.TextDisabled(rankText);
        ImGui.TextDisabled(state.TimingNote);
        if (state.Boosting)
            ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.35f, 1f), "加速中（诗/舞）");

        ImGui.End();
    }
}
