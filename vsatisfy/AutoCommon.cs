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

        // 第一次交互：与 NPC 交互并等待 SelectString 或 Supply 界面打开（最多重试 3 次）
        if (!Game.IsTurnInSupplyInProgress(npc))
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ErrorIf(!Game.InteractWith(npc.CraftData.TurnInInstanceId), "Failed to interact with turn-in NPC");

                // 等待 SelectString 或 Supply 界面打开（15 秒超时）
                var deadline = Environment.TickCount64 + 15000;
                while (Environment.TickCount64 < deadline)
                {
                    CancelToken.ThrowIfCancellationRequested();
                    Game.SkipTalk();
                    if (Game.IsTurnInSupplyInProgress(npc) || Game.IsSelectStringAddonActive())
                        break;
                    await Task.Delay(250, CancelToken);
                }

                if (Game.IsTurnInSupplyInProgress(npc) || Game.IsSelectStringAddonActive())
                    break;

                Service.Log.Warning($"TurnIn: 等待对话框超时（尝试 {attempt}/3），重试交互");
            }

            if (!Game.IsTurnInSupplyInProgress(npc) && !Game.IsSelectStringAddonActive())
                throw new Exception("TurnIn: 超过重试次数仍无法打开对话框");

            // 若弹出对话选项菜单，选择第一项（交换道具）
            if (Game.IsSelectStringAddonActive())
            {
                Game.SelectTurnIn();

                // 等待 Supply 界面打开（15 秒超时）
                var deadline = Environment.TickCount64 + 15000;
                while (Environment.TickCount64 < deadline)
                {
                    CancelToken.ThrowIfCancellationRequested();
                    Game.SkipTalk();
                    if (Game.IsTurnInSupplyInProgress(npc))
                        break;
                    await Task.Delay(250, CancelToken);
                }

                if (!Game.IsTurnInSupplyInProgress(npc))
                    throw new Exception("TurnIn: 选择对话框选项后超时无法打开 Supply 界面");
            }
        }

        while (npc.RemainingTurnins(slot) > 0)
        {
            Status = "交付中";

            // 等待 Supply 界面就绪（15 秒超时）
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                CancelToken.ThrowIfCancellationRequested();
                Game.SkipTalk();
                if (Game.IsSelectStringAddonActive()) Game.SelectTurnIn();
                if (npc.RemainingTurnins(slot) <= 0 || (Game.IsTurnInSupplyInProgress(npc) && Game.IsTurnInSupplyReady()))
                    break;
                await Task.Delay(250, CancelToken);
            }

            if (npc.RemainingTurnins(slot) <= 0)
                break;

            if (!(Game.IsTurnInSupplyInProgress(npc) && Game.IsTurnInSupplyReady()))
                throw new Exception("TurnIn: Supply 界面未就绪，无法继续交付");

            Game.TurnInSupply(slot);

            // 等待交付完成（30 秒超时）
            deadline = Environment.TickCount64 + 30000;
            while (Environment.TickCount64 < deadline)
            {
                CancelToken.ThrowIfCancellationRequested();
                if (npc.RemainingTurnins(slot) <= 0 || Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]))
                    break;
                await Task.Delay(250, CancelToken);
            }

            if (Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]))
                Game.TurnInRequestCommit(slot);
            else if (npc.RemainingTurnins(slot) > 0)
                throw new Exception("TurnIn: WaitHandIn 超时，交付请求未正常触发");
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
