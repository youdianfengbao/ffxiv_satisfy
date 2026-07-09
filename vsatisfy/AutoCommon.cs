using clib.Extensions;
using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
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

    protected async Task TurnIn(NPCInfo npc, int slot)
    {
        using var scope = BeginScope("TurnIn");
        if (npc.CraftData is null || npc.RemainingTurnins(slot) is 0) return;

        if (!Game.IsTurnInSupplyInProgress(npc))
        {
            ErrorIf(!Game.InteractWith(npc.CraftData.TurnInInstanceId), "Failed to interact with turn-in NPC");
            await WaitUntilSkipping(() => Game.IsTurnInSupplyInProgress(npc), "WaitDialog", UiSkipOptions.Talk);
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
