using clib.TaskSystem;
using Lumina.Excel.Sheets;
using System.Threading.Tasks;

namespace Satisfy;

// execute full fishing delivery: teleport to zone, fish, turn in
// TODO: automate actual fishing, use autohook
public sealed class AutoFish(NPCInfo npc) : AutoCommon
{
    protected override async Task Execute()
    {
        var remainingTurnins = npc.RemainingTurnins(2);
        if (remainingTurnins <= 0)
            return; // nothing to do

        if (npc.FishData == null || npc.CraftData == null)
            throw new Exception("Fish or turn-in data is not initialized");

        var turnInItemId = npc.FishData.FishItemId;
        var remainingFish = remainingTurnins - Game.NumItemsInInventory(turnInItemId, 1);
        if (remainingFish > 0)
        {
            Status = "前往渔点";
            await TeleportTo(npc.FishData.TerritoryTypeId, npc.FishData.Center);

            // TODO: improve move-to destination (ideally closest point where you can actually fish...)
            if (npc.FishData.IsSpearFish)
                Status = $"刺鱼 at {Service.LuminaRow<SpearfishingNotebook>(npc.FishData.FishSpotId)?.PlaceName.ValueNullable?.Name}";
            else
                Status = $"钓鱼 at {Service.LuminaRow<FishingSpot>(npc.FishData.FishSpotId)?.PlaceName.ValueNullable?.Name}";
            TrySprint();
            await MoveTo(npc.FishData.Center, MovementConfig.Everything.WithTolerance(10));
        }
        else // TODO: full auto...
        {
            Status = "前往交付地";
            // 图莱尤拉(1185): 禁止同区域二次以太之光传送
            await TeleportTo(npc.TerritoryId, npc.CraftData.TurnInLocation,
                allowSameZoneTeleport: npc.TerritoryId != 1185);

            Status = $"正在交付 {remainingTurnins}x {ItemName(turnInItemId)}";
            TrySprint();
            await MoveTo(npc.CraftData.TurnInLocation, MovementConfig.InteractRange);
            await TurnIn(npc, 2);
        }
    }
}
