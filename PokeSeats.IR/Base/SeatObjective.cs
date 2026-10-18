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
}

public readonly record struct SeatObjectiveId(Guid Value)
{
    public static SeatObjectiveId New() => new(Guid.NewGuid());
}

/// <summary>
/// 数字越小优先级越高；同优先级目标合并。缺失属性值的学生代价为零。
/// Centered/Bimodal 只允许 DistanceFromCenter；GroupBalance 忽略 Dimension。
/// </summary>
public sealed record SeatObjective(
    SeatObjectiveId Id,
    string DisplayName,
    PersonAttributeId AttributeId,
    SeatDimension Dimension,
    ObjectiveShape Shape,
    int Priority);
