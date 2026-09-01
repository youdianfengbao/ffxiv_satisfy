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
                    // 重选去重：只在 Supply agent 未激活或窗口无数据时才重选
                    if (Game.IsSelectStringAddonActive() && ShouldRetrySelectTurnIn()) Game.SelectTurnIn();
                    if (Game.IsTurnInSupplyInProgress(npc))
                        break;
                    await Task.Delay(250, CancelToken);
                }

                if (!Game.IsTurnInSupplyInProgress(npc))
                    throw new Exception("TurnIn: 选择对话框选项后超时无法打开 Supply 界面");
            }
        }

        // 重试计数器：最多重试 2 轮
        var retryCount = 0;
        const int maxRetries = 2;

        while (npc.RemainingTurnins(slot) > 0)
        {
            Status = "交付中";

            // 等待 Supply 界面可见且就绪（15 秒超时）
            var deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < deadline)
            {
                CancelToken.ThrowIfCancellationRequested();
                Game.SkipTalk();
                // 重选去重：只在 Supply agent 未激活或窗口无数据时才重选
                if (Game.IsSelectStringAddonActive() && ShouldRetrySelectTurnIn()) Game.SelectTurnIn();
                if (npc.RemainingTurnins(slot) <= 0 || Game.IsTurnInSupplyInProgress(npc))
                    break;
                await Task.Delay(250, CancelToken);
            }

            if (npc.RemainingTurnins(slot) <= 0)
                break;

            // 窗口不可见时的处理：关窗重试或报错
            if (!Game.IsTurnInSupplyInProgress(npc))
            {
                if (retryCount < maxRetries)
                {
                    Service.Log.Warning($"TurnIn: Supply 窗口不可见，关窗重试（重试 {retryCount + 1}/{maxRetries}）");
                    Game.CloseTurnInUi();
                    retryCount++;

                    // 等待窗口关闭（1 秒）
                    await Task.Delay(1000, CancelToken);

                    // 重新与 NPC 交互打开菜单
                    ErrorIf(!Game.InteractWith(npc.CraftData.TurnInInstanceId), "Failed to interact with turn-in NPC for retry");

                    // 等待 SelectString 或 Supply 界面打开（15 秒超时）
                    deadline = Environment.TickCount64 + 15000;
                    while (Environment.TickCount64 < deadline)
                    {
                        CancelToken.ThrowIfCancellationRequested();
                        Game.SkipTalk();
                        if (Game.IsTurnInSupplyInProgress(npc) || Game.IsSelectStringAddonActive())
                            break;
                        await Task.Delay(250, CancelToken);
                    }

                    // 若弹出对话选项菜单，选择第一项（交换道具）
                    if (Game.IsSelectStringAddonActive())
                    {
                        Game.SelectTurnIn();

                        // 等待 Supply 界面打开（15 秒超时）
                        deadline = Environment.TickCount64 + 15000;
                        while (Environment.TickCount64 < deadline)
                        {
                            CancelToken.ThrowIfCancellationRequested();
                            Game.SkipTalk();
                            if (Game.IsTurnInSupplyInProgress(npc))
                                break;
                            await Task.Delay(250, CancelToken);
                        }
                    }

                    // 重新开始交付循环
                    continue;
                }
                else
                {
                    Service.Log.Error("TurnIn: Supply 窗口不可见，超过重试次数，无法继续交付");
                    throw new Exception("TurnIn: Supply 窗口不可见，超过重试次数");
                }
            }

            // 窗口可见，执行交付
            Game.TurnInSupply(slot);

            // 等待交付请求（0.0.0.38 节流机制：等待交付请求触发）
            await WaitWhile(() => npc.RemainingTurnins(slot) > 0 && !Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]), "WaitHandIn");

            if (Game.IsTurnInRequestInProgress(npc.TurnInItems[slot]))
                Game.TurnInRequestCommit(slot);

            // 交付成功，重置重试计数
            retryCount = 0;
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
