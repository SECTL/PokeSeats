using PokeSeats.IR.Base;

namespace PokeSeats.Core.Graphing;

public interface IDistanceOracle
{
    /// <summary>
    /// 计算两座位之间的加权最短距离：普通邻接边代价为 1，跨窄过道的桥边代价为 2。
    /// </summary>
    /// <param name="p"></param>
    /// <param name="q"></param>
    /// <returns>路径边权之和的最小值；无路径时返回 null（宽过道不建桥边）。</returns>
    int? Distance(GridPos p, GridPos q);

    /// <summary>
    /// 闭球：与 <paramref name="center"/> 的加权图距离不超过 <paramref name="radius"/> 的所有座位（含 center 自身）。
    /// </summary>
    /// <remarks>
    /// 图节点只有座位，因此不在图中的坐标（过道、讲台、越界）不是可查询的位置：
    /// 对这样的坐标 <see cref="Distance"/> 返回 null，<see cref="Ball(GridPos, int)"/> 返回空集。
    /// 负半径返回空集；半径 0 返回 center 自身；结果不含重复项。
    /// </remarks>
    IEnumerable<GridPos> Ball(GridPos center, int radius);

    /// <summary>
    /// 多源闭球：与 <paramref name="centers"/> 中任一处的加权图距离不超过 <paramref name="radius"/> 的所有座位，
    /// 等价于各单源球的并集。
    /// </summary>
    IEnumerable<GridPos> Ball(IReadOnlySet<GridPos> centers, int radius);

}
