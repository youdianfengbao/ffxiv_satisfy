using clib.Extensions;
using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;
using System.Threading.Tasks;

namespace Satisfy;

// common automation utilities
public abstract class AutoCommon : TaskBase
{
    /// <summary>尝试使用冲刺, 冷却中则忽略</summary>
    protected static void TrySprint()
    {
        unsafe
        {
            if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 4) == 0)
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 4);
        }
    }

    /// <summary>检查是否需要重选 SelectTurnIn（重选去重：若 Supply agent 已 active 且按名字窗口 Ready，则不再重发点击）</summary>
    protected static bool ShouldRetrySelectTurnIn()
    {
        unsafe
        {
            var agent = AgentSatisfactionSupply.Instance();
            if (agent != null && agent->IsAgentActive())
            {
                var addonByName = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("SatisfactionSupply");
                if (addonByName != null && addonByName->AtkValues != null)
                {
                    // Supply agent 已 active 且窗口已有数据，不再重选
                    return false;
                }
            }
        }
        return true; // agent 未激活或窗口无数据，允许重选
    }

    /// <summary>
    /// 使用自建 vnavmesh 寻路移动到目标点。
    /// 替代 clib 的 MoveTo（其内部 SimpleMove.PathfindAndMoveCloseTo 的 range=3 触发 vnavmesh
    /// GoalRadiusHeuristic 负启发式，在障碍物场景会撞墙）。失败时抛异常，语义与 clib MoveTo 一致。
    /// </summary>
    /// <param name="dest">目标点（世界坐标）</param>
    /// <param name="stopDistance">停止距离（米），默认 1.3（参考 Questionable 交互停止距离）</param>
    /// <param name="allowFly">允许飞行路径（仅已坐骑/飞行时生效，否则退回步行）</param>
    protected Task MoveToDestination(Vector3 dest, float stopDistance = 1.3f, bool allowFly = false) =>
        Movement.MoveTo(dest, stopDistance, allowFly, CancelToken);

    protected async Task TurnIn(NPCInfo npc, int slot)
    {
        using var scope = BeginScope("TurnIn");
        try
        {
            await TurnInCore(npc, slot);
        }
        catch (Exception)
        {
            // 任务失败时把挂起的对话/交付窗口关掉, 用户无需手动 ESC
            Game.CloseTurnInUi();
            throw;
        }
    }

    private async Task TurnInCore(NPCInfo npc, int slot)
    {
        if (npc.CraftData is null || npc.RemainingTurnins(slot) is 0) return;

        // 【0.0.0.38 原文】清场后 vsatisfy 自己交互 NPC 从头走交付链
        ErrorIf(!Game.InteractWith(npc.CraftData.TurnInInstanceId), "Failed to interact with turn-in NPC");

        // 跳过 Talk 对话；等待提交界面打开或对话选项菜单弹出
        await WaitUntilSkipping(() => Game.IsTurnInSupplyInProgress(npc) || Game.IsSelectStringAddonActive(), "WaitDialog", UiSkipOptions.Talk);

        // 若弹出对话选项菜单，选择第一项（交换道具）
        if (Game.IsSelectStringAddonActive())
        {
            Game.SelectTurnIn();
            await WaitUntilSkipping(() => Game.IsTurnInSupplyInProgress(npc), "WaitDialog", UiSkipOptions.Talk);
        }

        while (npc.RemainingTurnins(slot) > 0)
        {
            Status = "交付中";
            Service.Log.Debug($"TurnIn: 交付循环开始，RemainingTurnins={npc.RemainingTurnins(slot)}");

            await WaitUntilSkipping(() => npc.RemainingTurnins(slot) <= 0 || Game.IsTurnInSupplyInProgress(npc), "WaitDialog", UiSkipOptions.Talk);
            if (npc.RemainingTurnins(slot) <= 0)
                break;

            // 【细粒度日志】TurnInSupply 前的状态
            var windowVisible = Game.IsTurnInSupplyInProgress(npc);
            Service.Log.Debug($"TurnIn: TurnInSupply 调用前，WindowVisible={windowVisible}, RemainingTurnins={npc.RemainingTurnins(slot)}");

            Game.TurnInSupply(slot);

            // 【细粒度日志】TurnInSupply 后的状态
            windowVisible = Game.IsTurnInSupplyInProgress(npc);
            Service.Log.Debug($"TurnIn: TurnInSupply 调用后，WindowVisible={windowVisible}");

            await WaitWhile(() => npc.RemainingTurnins(slot) > 0 && !Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]), "WaitHandIn");

            // 【细粒度日志】Commit 前的请求状态
            if (Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]))
            {
                Service.Log.Debug($"TurnIn: 交付请求进行中，准备提交，ItemId={npc.TurnInItems[slot]}");
                Game.TurnInRequestCommit(slot);
            }
        }

        await WaitForCutscene();
    }

    protected async Task WaitForCutscene()
    {
        using var scope = BeginScope(nameof(WaitForCutscene));
        Status = "等待过场动画";
        await WaitUntilSkipping(() => Service.Conditions[ConditionFlag.OccupiedInCutSceneEvent], "WaitCutsceneStart", UiSkipOptions.Talk);
        await WaitUntilSkipping(() => !Service.Conditions[ConditionFlag.OccupiedInCutSceneEvent], "WaitCutsceneEnd", UiSkipOptions.Talk);
    }

    protected static string ItemName(uint itemId) => Service.LuminaRow<Lumina.Excel.Sheets.Item>(itemId)?.Name.ToString() ?? itemId.ToString();
}
