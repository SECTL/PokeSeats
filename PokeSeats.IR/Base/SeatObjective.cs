namespace PokeSeats.IR.Base;

/// <summary>排座软目标沿座位空间的哪个轴评分。</summary>
public enum SeatDimension
{
    Row,
    Column,
    DistanceFromCenter,
}

/// <summary>属性排序与目标形状。</summary>
public enum ObjectiveShape
{
    AscendingToFront,
    DescendingToFront,
    Centered,
    Bimodal,
    GroupBalance,
    RowBalance,
    RegionPreference,
}

/// <summary>区域偏好优先安排较高或较低的已知数值。</summary>
public enum RegionPreferenceDirection { HighValues, LowValues }

/// <summary>包含端点的 0-based 矩形；边界必须非负且最小值不大于最大值。</summary>
public readonly record struct SeatRegion(int RowMin, int RowMax, int ColMin, int ColMax)
{
    public bool Contains(GridPos position) => position.Row >= RowMin && position.Row <= RowMax
        && position.Col >= ColMin && position.Col <= ColMax;
}

public readonly record struct SeatObjectiveId(Guid Value)
{
    public static SeatObjectiveId New() => new(Guid.NewGuid());
}

/// <summary>
/// 数字越小优先级越高；同优先级目标合并。缺失属性不产生代价，也不计入各排行和。
/// Centered/Bimodal 只允许 DistanceFromCenter。普通形状可限定区域，区域外座位同权且排在区域内之后。
/// RegionPreference 必须指定区域，按方向优先将较高或较低数值安排在区域内，区域外不排序。
/// RowBalance 固定使用 Row 且不允许区域，按各排 Desk 容量比例平衡已知属性总量；同值时平衡已知人数。
/// </summary>
public sealed record SeatObjective(
    SeatObjectiveId Id,
    string DisplayName,
    PersonAttributeId AttributeId,
    SeatDimension Dimension,
    ObjectiveShape Shape,
    int Priority,
    SeatRegion? Region = null,
    RegionPreferenceDirection RegionPreferenceDirection = RegionPreferenceDirection.HighValues);
