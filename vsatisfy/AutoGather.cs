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
        {
            await Gather();
        }
        else
        {
            // 背包充足、跳过采集直接去交付的路径：检查并切换职业
            await EnsureCorrectJob();
        }

        // Questionable 可能顺带把交付做完，如果已交付完成则直接结束
        if (npc.RemainingTurnins(1) <= 0)
            return;

        // 已在目标地图时跳过传送直接寻路(与 AutoCraft 一致); 不同区才传送
        // allowSameZoneTeleport: false —— 图莱尤拉(1185)等多层地图禁止同区域二次以太之光传送, 否则寻路会出错
        if (Service.ClientState.TerritoryType != npc.TerritoryId)
        {
            Status = "传送回 Npc 处";
            await TeleportTo(npc.TerritoryId, npc.CraftData.TurnInLocation, allowSameZoneTeleport: false);
        }

        Status = "前往 Npc 处";
        TrySprint();
        await MoveToDestination(npc.CraftData.TurnInLocation);
        Status = $"正在交付 {remainingTurnins}x {ItemName(npc.TurnInItems[1])}";
        await TurnIn(npc, 1);
    }

    private async Task Gather()
    {
        Status = "使用 Questionable 进行采集";
        using var scope = BeginScope("Gathering");
        using var stop = new OnDispose(() => _stop.InvokeFunc($"{Service.PluginInterface.Manifest.InternalName}"));
        ErrorIf(!_startGathering.InvokeFunc(npc.TurninId, npc.GatherData!.GatherItemId, (byte)npc.GatherData.ClassJobId, npc.RemainingTurnins(1), (ushort)npc.GatherData.CollectabilityHigh), "Unable to invoke Questionable");

        // 等待 Questionable 启动（15 秒超时）
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            CancelToken.ThrowIfCancellationRequested();
            if (_isRunning.InvokeFunc())
                break;
            await Task.Delay(250, CancelToken);
        }
        if (!_isRunning.InvokeFunc())
            throw new Exception("Timed out waiting for Questionable to start");

        // 等待 Questionable 完成（条件：Questionable 在跑 且 本 NPC 还有剩余交付次数）
        // Questionable 可能顺带把交付做完，所以需要检查 RemainingTurnins
        while (_isRunning.InvokeFunc() && npc.RemainingTurnins(1) > 0)
        {
            CancelToken.ThrowIfCancellationRequested();
            await Task.Delay(250, CancelToken);
        }
    }

    private async Task EnsureCorrectJob()
    {
        if (npc.GatherData == null)
            return;

        var requiredJobId = npc.GatherData.ClassJobId;
        if (requiredJobId == 0)
            return; // 没有职业要求

        // 检查当前职业
        var currentJobId = GetCurrentJobId();
        if (currentJobId == requiredJobId)
            return; // 职业已正确，无需切换

        Service.Log.Warning($"AutoGather: 当前职业 {currentJobId} 与所需职业 {requiredJobId} 不匹配，尝试切换职业");

        // 【改用装备组切换】遍历所有装备组，找到 ClassJobId 匹配的装备组
        int matchingGearsetIndex = -1;
        unsafe
        {
            var gearsetModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                Service.Log.Error("AutoGather: 无法获取 RaptureGearsetModule 实例");
                throw new Exception("AutoGather: 职业切换失败 - 无法获取装备组模块");
            }

            // 遍历所有装备组（通常有 100 个槽位，实际使用的较少）
            for (int i = 0; i < 100; i++)
            {
                var gearset = gearsetModule->GetGearset(i);
                if (gearset == null)
                    continue;

                // 检查装备组 ID 是否大于0（表示有效装备组）
                if (gearset->Id <= 0)
                    continue;

                // 检查职业是否匹配（通过装备物品间接判断，或使用 GearsetEntry 的其他属性）
                // 由于 ClassJobId 属性可能不可用，我们使用装备组的 ClassJob 字段
                var classJobId = gearset->ClassJob;
                if (classJobId == requiredJobId)
                {
                    matchingGearsetIndex = i;
                    Service.Log.Info($"AutoGather: 找到匹配装备组，索引={i}，职业={requiredJobId}");
                    break;
                }
            }
        }

        if (matchingGearsetIndex == -1)
        {
            Service.Log.Error($"AutoGather: 未找到职业 {requiredJobId} 对应的装备组");
            throw new Exception($"AutoGather: 职业切换失败 - 未找到职业 {requiredJobId} 对应的装备组，请先在游戏中创建该职业的装备组");
        }

        unsafe
        {
            var gearsetModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            var targetGearset = gearsetModule->GetGearset(matchingGearsetIndex);
            Service.Log.Info($"AutoGather: 正在装备 {targetGearset->Name.ToString()}（索引 {matchingGearsetIndex}）");

            // 使用装备组模块装备（这个方法会立即切换职业）
            gearsetModule->EquipGearset(matchingGearsetIndex);
        }

        // 等待职业切换生效（最多5秒）
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            CancelToken.ThrowIfCancellationRequested();
            await Task.Delay(250, CancelToken);

            var newJobId = GetCurrentJobId();
            if (newJobId == requiredJobId)
            {
                Service.Log.Info($"AutoGather: 成功切换到职业 {newJobId}");
                return;
            }
        }

        // 装备组切换失败（理论上不应该发生，除非装备组数据损坏）
        Service.Log.Error($"AutoGather: 装备组切换超时（当前仍为 {GetCurrentJobId()}）");
        throw new Exception($"AutoGather: 职业切换失败 - 装备组切换超时，请检查装备组索引 {matchingGearsetIndex} 是否有效");
    }

    private unsafe uint GetCurrentJobId()
    {
        return Service.PlayerState.ClassJob.RowId;
    }
}
