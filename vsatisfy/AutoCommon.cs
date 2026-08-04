using clib.Extensions;
using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
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
        if (npc.CraftData is null || npc.RemainingTurnins(slot) is 0) return;

        if (!Game.IsTurnInSupplyInProgress(npc))
        {
            ErrorIf(!Game.InteractWith(npc.CraftData.TurnInInstanceId), "Failed to interact with turn-in NPC");
            // 跳过 Talk 对话；等待提交界面打开或对话选项菜单弹出
            await WaitUntilSkipping(() => Game.IsTurnInSupplyInProgress(npc) || Game.IsSelectStringAddonActive(), "WaitDialog", UiSkipOptions.Talk);
            // 若弹出对话选项菜单，选择第一项（交换道具）
            if (Game.IsSelectStringAddonActive())
            {
                Game.SelectTurnIn();
                await WaitUntilSkipping(() => Game.IsTurnInSupplyInProgress(npc), "WaitDialog", UiSkipOptions.Talk);
            }
        }
        while (npc.RemainingTurnins(slot) > 0)
        {
            Status = "交付中";
            await WaitUntilSkipping(() => npc.RemainingTurnins(slot) <= 0 || Game.IsTurnInSupplyInProgress(npc), "WaitDialog", UiSkipOptions.Talk);
            if (npc.RemainingTurnins(slot) <= 0)
                break;

            Game.TurnInSupply(slot);
            await WaitWhile(() => npc.RemainingTurnins(slot) > 0 && !Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]), "WaitHandIn");
            if (Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]))
                Game.TurnInRequestCommit(slot);
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
