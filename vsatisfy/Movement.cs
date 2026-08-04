using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Satisfy;

/// <summary>
/// 自建 vnavmesh 寻路（复刻 Questionable 的 MovementController 模式）。
/// 使用 Nav.PathfindCancelable（range=0，标准 A* 启发式）+ Path.MoveTo 沿路径移动，
/// 避开 clib 的 SimpleMove.PathfindAndMoveCloseTo(dest, fly, 3) —— 其 range=3 会触发 vnavmesh
/// 的 GoalRadiusHeuristic 负启发式（终点 3m 半径内启发式代价为 -1），在柜台等障碍物场景
/// 生成非法绕路路径导致角色撞墙卡死。
/// </summary>
public static class Movement
{
    private static readonly ICallGateSubscriber<bool> NavIsReady =
        Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private static readonly ICallGateSubscriber<float> NavBuildProgress =
        Service.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
    private static readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> NavPathfindCancelable =
        Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
    private static readonly ICallGateSubscriber<bool> PathIsRunning =
        Service.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    private static readonly ICallGateSubscriber<List<Vector3>> PathListWaypoints =
        Service.PluginInterface.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
    private static readonly ICallGateSubscriber<List<Vector3>, bool, object> PathMoveTo =
        Service.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
    private static readonly ICallGateSubscriber<object> PathStop =
        Service.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");

    /// <summary>最大重算次数（参考 Questionable RecalculateNavmesh 上限）</summary>
    private const int MaxRecalculations = 10;
    /// <summary>卡住判定窗口（毫秒）：该窗口内位移小于 StuckTolerance 视为卡住</summary>
    private const int StuckTimeoutMs = 500;
    /// <summary>卡住判定位移阈值（米）</summary>
    private const float StuckTolerance = 0.5f;
    /// <summary>单次寻路超时</summary>
    private static readonly TimeSpan PathfindTimeout = TimeSpan.FromSeconds(30);
    /// <summary>等待 navmesh 就绪超时（传送后新地图需要时间构建）</summary>
    private static readonly TimeSpan NavmeshReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 寻路移动到目标点。到达 stopDistance 内即返回；多次重算仍无法到达时抛异常（与 clib MoveTo 失败语义一致）。
    /// </summary>
    /// <param name="dest">目标点（世界坐标）</param>
    /// <param name="stopDistance">停止距离（米）</param>
    /// <param name="allowFly">允许飞行路径（仅当玩家已坐骑/飞行中才使用飞行寻路，否则自动退回步行）</param>
    /// <param name="ct">取消令牌（任务停止时中断）</param>
    public static async Task MoveTo(Vector3 dest, float stopDistance = 1.3f, bool allowFly = false, CancellationToken ct = default)
    {
        await WaitNavmeshReady(ct);

        // 飞行路径仅当玩家确实在坐骑/飞行状态时使用（参考 Questionable：未坐骑时退回步行路径）
        bool fly = allowFly &&
            (Service.Conditions[ConditionFlag.Mounted] || Service.Conditions[ConditionFlag.InFlight]);

        for (int attempt = 0; attempt <= MaxRecalculations; ++attempt)
        {
            ct.ThrowIfCancellationRequested();
            if (Vector3.Distance(Game.PlayerPosition(), dest) < stopDistance)
                return;

            var path = await Pathfind(Game.PlayerPosition(), dest, fly, ct);
            if (path.Count == 0)
            {
                Service.Log.Warning("vnavmesh: no path found to {Dest} (attempt {Attempt})", dest, attempt + 1);
                continue; // 重算（飞行状态/起点可能变化）
            }

            if (await FollowPath(path, dest, stopDistance, fly, ct))
                return;

            Service.Log.Warning("vnavmesh: stuck, recalculating path (attempt {Attempt})", attempt + 1);
        }
        throw new Exception($"Failed to reach {dest} after {MaxRecalculations + 1} attempts");
    }

    /// <summary>等待 vnavmesh navmesh 就绪（含传送后新地图加载）</summary>
    private static async Task WaitNavmeshReady(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)NavmeshReadyTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (NavIsReady.InvokeFunc())
                    return;
                if (NavBuildProgress.InvokeFunc() < 0)
                    throw new Exception("vnavmesh navmesh failed to build");
            }
            catch (IpcNotReadyError)
            {
                throw new Exception("vnavmesh is not installed or not loaded; cannot navigate");
            }
            await Task.Delay(500, ct);
        }
        throw new Exception("Timed out waiting for vnavmesh navmesh to load");
    }

    /// <summary>显式寻路（range=0，标准启发式）。返回空列表表示无法寻路。</summary>
    private static async Task<List<Vector3>> Pathfind(Vector3 start, Vector3 dest, bool fly, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PathfindTimeout);
        try
        {
            return await NavPathfindCancelable.InvokeFunc(start, dest, fly, cts.Token) ?? [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (IpcNotReadyError)
        {
            throw new Exception("vnavmesh is not installed or not loaded; cannot navigate");
        }
        catch (Exception e)
        {
            Service.Log.Error(e, "vnavmesh pathfind failed");
            return [];
        }
    }

    /// <summary>
    /// 沿路径移动并做脱卡检测。返回 true 表示已到达；false 表示卡住需外层重算。
    /// </summary>
    private static async Task<bool> FollowPath(List<Vector3> path, Vector3 dest, float stopDistance, bool fly, CancellationToken ct)
    {
        try
        {
            Stop();
            PathMoveTo.InvokeAction(path, fly);
        }
        catch (IpcNotReadyError)
        {
            throw new Exception("vnavmesh is not installed or not loaded; cannot navigate");
        }

        var lastPos = Game.PlayerPosition();
        var lastMovement = Environment.TickCount64;
        int stuckCount = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pos = Game.PlayerPosition();
            if (Vector3.Distance(pos, dest) < stopDistance)
            {
                Stop();
                return true;
            }

            bool running;
            try
            {
                running = PathIsRunning.InvokeFunc();
            }
            catch (IpcNotReadyError)
            {
                throw new Exception("vnavmesh is not installed or not loaded; cannot navigate");
            }
            if (!running)
                return Vector3.Distance(Game.PlayerPosition(), dest) < stopDistance; // vnavmesh 已停止（到达或失败）

            var now = Environment.TickCount64;
            if (Vector3.Distance(pos, lastPos) >= StuckTolerance)
            {
                lastPos = pos;
                lastMovement = now;
                stuckCount = 0;
            }
            else if (now - lastMovement > StuckTimeoutMs)
            {
                lastPos = pos;
                lastMovement = now;
                ++stuckCount;
                if (stuckCount % 6 == 1)
                {
                    // 跳跃脱卡（参考 Questionable：第 6 次卡住时跳跃尝试解决）
                    Service.Log.Warning("vnavmesh: stuck, jumping (n={N})", stuckCount);
                    Game.UseAction(ActionType.GeneralAction, 2);
                }
                else
                {
                    Service.Log.Warning("vnavmesh: stuck, restarting path (n={N})", stuckCount);
                    Stop();
                    return false; // 触发外层重算
                }
            }
            await Task.Delay(100, ct);
        }
    }

    private static void Stop()
    {
        try
        {
            PathStop.InvokeAction();
        }
        catch
        {
            // vnavmesh 卸载等场景忽略
        }
    }
}
