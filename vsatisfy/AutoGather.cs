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

        // 统一交给 Questionable 处理采集与交付
        await QuestionableGatherAndTurnIn();
    }

    private async Task QuestionableGatherAndTurnIn()
    {
        Status = "使用 Questionable 进行采集与交付";
        using var scope = BeginScope("GatheringAndTurnIn");
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

        // 等待 Questionable 完成采集（超时 30 分钟）
        var completionDeadline = Environment.TickCount64 + 1800000; // 30 分钟
        while (_isRunning.InvokeFunc())
        {
            CancelToken.ThrowIfCancellationRequested();

            // 超时检查
            if (Environment.TickCount64 >= completionDeadline)
            {
                Service.Log.Error("AutoGather: Questionable 执行超时（30 分钟），中止任务");
                throw new Exception("Questionable execution timeout (30 minutes)");
            }

            await Task.Delay(250, CancelToken);
        }

        // Questionable 停止后，若还有剩余交付次数则由 vsatisfy 自建交付收尾
        if (npc.RemainingTurnins(1) > 0)
        {
            Service.Log.Info($"AutoGather: Questionable 采集完成，剩余 {npc.RemainingTurnins(1)} 次交付，开始自建交付");
            Status = "执行自建交付";
            await TurnIn(npc, 1);
        }

        // 最终验证交付完成
        if (npc.RemainingTurnins(1) > 0)
        {
            Service.Log.Error($"AutoGather: 交付完成后仍有剩余交付次数 {npc.RemainingTurnins(1)}，交付失败");
            throw new Exception($"TurnIn completed but {npc.RemainingTurnins(1)} turnins remaining");
        }
    }
}
