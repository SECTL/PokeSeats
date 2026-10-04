namespace PokeSeats.IR.Base;

/// <summary>
/// 距离的度量方式：切比雪特（允许斜角）/曼哈顿（不允许斜角）
/// </summary>
public enum DistanceMetric
{
    Chebyshev,
    Manhattan,
};

public enum RelationKind
{
    Proximity,     // 无方向，纯距离
    Behind, InFrontOf, LeftOf, RightOf,
    SameRow, SameColumn, // 轴锁定（"必须在同一排"）
    ExactOffset,         // 精确偏移
}

