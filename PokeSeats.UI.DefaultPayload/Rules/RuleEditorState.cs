using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class RuleEditorState
{
    private static readonly PersonId PreviewRelativePerson = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly PersonId PreviewCenterPerson = new(Guid.Parse("00000000-0000-0000-0000-000000000002"));
    public int Category = 2;
    // 编辑器语义：Person 是中心 A，Reference 是相对于 A 定位的对象 B。
    // IR 的 RelationRule 仍按 A 相对于 B 存储，Build/Load 在边界处交换两者。
    public PersonId? Person;
    public PersonId? Reference;
    public bool Negated;
    public RelationKind Relation = RelationKind.ExactOffset;
    public int Dx;
    public int Dy = 1;
    public AdjacencyMode Adjacency = AdjacencyMode.FourWay;
    public bool AllowNarrowAisle = true;
    public GridPos? First;
    public GridPos? Last;
    public AxisType AxisType;
    public int AxisIndex;
    public bool UseMin = true;
    public bool UseMax = true;
    public int Min;
    public int Max = 1;
    public RuleId? Editing;
    private Rule? cachedRule;
    private int layoutRevision = -1;
    private RuleSupport? support;
    public HashSet<GridPos> Allowed { get; } = new();
    public HashSet<GridPos> ReferenceSeats { get; } = new();
    public GridPos? PreviewCenter;
    public GridPos? PreviewAnchor { get; private set; }
    private GridPos? cachedReference;

    public Rule? Build()
    {
        RuleId id = Editing ?? new RuleId(Guid.Empty);
        RulePolarity polarity = Negated ? RulePolarity.Forbidden : RulePolarity.Required;
        return Category switch
        {
            0 when Person is { } person && First is { } pos => new PinnedRule(id, "", person, pos),
            1 when Person is { } person && First is { } start && Last is { } end => new RegionRule(id, "", person, polarity,
                Math.Min(start.Row, end.Row), Math.Max(start.Row, end.Row), Math.Min(start.Col, end.Col), Math.Max(start.Col, end.Col)),
            // UI: B = A + (Dx, Dy). IR: model A is the relative person and model B is the center.
            2 when Person is { } center && Reference is { } relative && center != relative => new RelationRule(id, "", relative, center,
                Relation, polarity, DistanceMetric.Manhattan,
                Relation == RelationKind.Proximity ? 1 : null, Dx, Dy, Adjacency, AllowNarrowAisle),
            3 when (UseMin || UseMax) && (!UseMin || Min >= 0) && (!UseMax || Max >= 0) && (!UseMin || !UseMax || Min <= Max)
                => new CapacityRule(id, "", new Axis(AxisType, AxisIndex), UseMin ? Min : null, UseMax ? Max : null),
            _ => null
        };
    }

    public Rule? BuildPreview(RelationKind? preview = null)
    {
        if (Category != 2) return Build();
        // Geometric preview is independent of roster selection and cannot be saved.
        RelationKind kind = preview ?? Relation;
        return new RelationRule(
            Editing ?? new RuleId(Guid.Empty), "",
            PreviewRelativePerson, PreviewCenterPerson, kind,
            Negated ? RulePolarity.Forbidden : RulePolarity.Required,
            DistanceMetric.Manhattan,
            kind == RelationKind.Proximity ? 1 : null,
            Dx, Dy, Adjacency, AllowNarrowAisle);
    }

    public void Load(Rule rule)
    {
        Editing = rule.Id;
        switch (rule)
        {
            case PinnedRule pin:
                Category = 0; Person = pin.Person; First = Last = pin.Pos; Negated = false; break;
            case RegionRule region:
                Category = 1; Person = region.Person; Negated = region.Polarity == RulePolarity.Forbidden;
                First = new GridPos(region.RowMin ?? 0, region.ColMin ?? 0);
                Last = new GridPos(region.RowMax ?? int.MaxValue, region.ColMax ?? int.MaxValue); break;
            case RelationRule relation:
                // Stored IR is A relative to B; expose it as B (center A) and A (relative B).
                Category = 2; Person = relation.B; Reference = relation.A; Relation = relation.Kind;
                Negated = relation.Polarity == RulePolarity.Forbidden; Dx = relation.OffsetDx; Dy = relation.OffsetDy;
                Adjacency = relation.Adjacency; AllowNarrowAisle = relation.AllowNarrowAisle;
                PreviewCenter = null; break;
            case CapacityRule capacity:
                Category = 3; AxisType = capacity.Axis.Type; AxisIndex = capacity.Axis.Index;
                UseMin = capacity.Min.HasValue; UseMax = capacity.Max.HasValue; Min = capacity.Min ?? 0; Max = capacity.Max ?? 0; break;
        }
    }

    public void UpdatePreview(LayoutDocument layout, Rule? rule)
    {
        bool changedLayout = layoutRevision != layout.Revision;
        if (!changedLayout && Equals(rule, cachedRule) && PreviewCenter == cachedReference)
            return;
        if (changedLayout || support is null)
        {
            support = new RuleSupport(layout.ToGrid());
            layoutRevision = layout.Revision;
        }
        cachedRule = rule;
        cachedReference = PreviewCenter;
        Allowed.Clear(); ReferenceSeats.Clear();
        PreviewAnchor = null;
        if (rule is RelationRule relation)
        {
            var desks = layout.ToGrid().GetDeskCells().ToArray();
            if (desks.Length > 0)
            {
                PreviewAnchor = PreviewCenter is { } selected && desks.Contains(selected) ? selected : desks[0];
                var matching = new HashSet<(GridPos A, GridPos B)>(support.GetPairs(relation));
                bool required = relation.Polarity == RulePolarity.Required;
                foreach (GridPos center in desks)
                    foreach (GridPos relative in desks)
                    {
                        if (center == relative) continue;
                        // relation.A is UI B and relation.B is UI A.
                        bool positive = matching.Contains((relative, center));
                        if (positive != required) continue;
                        ReferenceSeats.Add(center);
                        if (PreviewAnchor == center) Allowed.Add(relative);
                    }
            }
        }
        else if (rule is PinnedRule or RegionRule)
        {
            Allowed.UnionWith(support.GetPositions(rule));
            if (rule is RegionRule { Polarity: RulePolarity.Forbidden })
            {
                var positive = new HashSet<GridPos>(Allowed); Allowed.Clear();
                foreach (GridPos desk in layout.ToGrid().GetDeskCells())
                    if (!positive.Contains(desk)) Allowed.Add(desk);
            }
        }
    }

    public bool MatchesOffset(int row, int col, RelationKind? preview = null)
    {
        if (row == 0 && col == 0) return false;
        bool matches = MatchesBaseOffset(row, col, preview ?? Relation);
        return Negated ? !matches : matches;
    }

    public bool MatchesBaseOffset(int row, int col, RelationKind kind)
    {
        if (row == 0 && col == 0) return false;
        return kind switch
        {
            RelationKind.ExactOffset => row == Dy && col == Dx,
            RelationKind.Behind => row > 0,
            RelationKind.InFrontOf => row < 0,
            RelationKind.LeftOf => col < 0,
            RelationKind.RightOf => col > 0,
            RelationKind.SameRow => row == 0,
            RelationKind.SameColumn => col == 0,
            RelationKind.Proximity => AdjacencyGeometry.Matches(Adjacency, row, col),
            _ => false
        };
    }
}
