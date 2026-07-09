using clib.TaskSystem;
using Dalamud.Plugin.Ipc;
using System.Threading.Tasks;

namespace Satisfy;

public sealed class AutoGather(NPCInfo npc) : AutoCommon
{
    private readonly ICallGateSubscriber<bool> _isRunning = Service.PluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
    private readonly ICallGateSubscriber<string, bool> _stop = Service.PluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
    // uint npcId, uint itemId, byte classJob = ((byte)Job.MIN), int quantity = 1, ushort collectability = 0
    private readonly ICallGateSubscriber<uint, uint, byte, int, ushort, bool> _startGathering = Service.PluginInterface.GetIpcSubscriber<uint, uint, byte, int, ushort, bool>("Questionable.StartGatheringComplex");
    protected override async Task Execute()
    {
        var remainingTurnins = npc.RemainingTurnins(1);
        if (remainingTurnins <= 0)
            return; // nothing to do

        if (npc.GatherData == null || npc.CraftData == null)
            throw new Exception("Gather or turn-in data is not initialized");

        if (remainingTurnins - Game.NumItemsInInventory(npc.GatherData.GatherItemId, (short)npc.GatherData.CollectabilityLow) > 0)
            await Gather();

        Status = "传送回 Npc 处";
        // 图莱尤拉(1185): 禁止同区域二次以太之光传送, 多层地图寻路会出错
        await TeleportTo(npc.TerritoryId, npc.CraftData.TurnInLocation,
            allowSameZoneTeleport: npc.TerritoryId != 1185);

        Status = "前往 Npc 处";
        TrySprint();
        await MoveTo(npc.CraftData.TurnInLocation, MovementConfig.InteractRange);
        Status = $"正在交付 {remainingTurnins}x {ItemName(npc.TurnInItems[1])}";
        await TurnIn(npc, 1);
    }

    private async Task Gather()
    {
        Status = "使用 Questionable 进行采集";
        using var scope = BeginScope("Gathering");
        using var stop = new OnDispose(() => _stop.InvokeFunc($"{Service.PluginInterface.Manifest.InternalName}"));
        ErrorIf(!_startGathering.InvokeFunc(npc.TurninId, npc.GatherData!.GatherItemId, (byte)npc.GatherData.ClassJobId, npc.RemainingTurnins(1), (ushort)npc.GatherData.CollectabilityHigh), "Unable to invoke Questionable");
        await WaitWhile(() => !_isRunning.InvokeFunc(), "Waiting for gathering to start");
        await WaitWhile(_isRunning.InvokeFunc, "Waiting for gathering to finish");
    }
}
