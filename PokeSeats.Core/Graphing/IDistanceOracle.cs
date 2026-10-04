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

    IEnumerable<GridPos> Ball(GridPos               center,  int radius);
    IEnumerable<GridPos> Ball(IReadOnlySet<GridPos> centers, int radius);

}
