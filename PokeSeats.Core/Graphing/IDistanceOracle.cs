using PokeSeats.IR.Base;

namespace PokeSeats.Core.Graphing;

public interface IDistanceOracle
{
    /// <summary>
    /// 计算两点之间的距离
    /// </summary>
    /// <param name="p"></param>
    /// <param name="q"></param>
    /// <returns>根据图类型返回最短距离，不可达时返回 null</returns>
    int? Distance(GridPos p, GridPos q);

    /// <summary>
    /// 闭球：与 <paramref name="center"/> 的图距离不超过 <paramref name="radius"/> 的所有座位（含 center 自身）。
    /// </summary>
    /// <remarks>
    /// 图节点只有座位，因此不在图中的坐标（过道、讲台、越界）不是可查询的位置：
    /// 对这样的坐标 <see cref="Distance"/> 返回 null，<see cref="Ball(GridPos, int)"/> 返回空集。
    /// 负半径返回空集；半径 0 返回 center 自身；结果不含重复项。
    /// </remarks>
    IEnumerable<GridPos> Ball(GridPos center, int radius);

    /// <summary>
    /// 多源闭球：与 <paramref name="centers"/> 中任一处的图距离不超过 <paramref name="radius"/> 的所有座位，
    /// 等价于各单源球的并集。
    /// </summary>
    IEnumerable<GridPos> Ball(IReadOnlySet<GridPos> centers, int radius);

}
