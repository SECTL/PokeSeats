using System.Text.Json;
using System.Text.Json.Serialization;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>可编辑的规则集合，用显式 DTO 做 JSON 往返（.NET 6 没有多态序列化）。</summary>
/// <remarks>
/// <para>
/// 每种规则用 <c>Type</c> 判别字段加一个专属数据对象；规则标识 <c>Id</c> 是稳定 Guid，
/// 编辑既有规则时沿用原 Id，界面据此关联。保存走临时文件加 <see cref="File.Move(string, string, bool)"/>，
/// 与 <see cref="LayoutDocument"/> 一致。
/// </para>
/// <para>
/// 加载失败时不改动原文件，返回空文档并通过 <c>loadMessage</c> 报告原因；
/// 标识缺失、标识重复、判别字段未知或数据对象缺失都按文件损坏处理。
/// </para>
/// </remarks>
internal sealed class RuleDocument
{
    private const string RelationType = "relation";
    private const string PinnedType = "pinned";
    private const string CapacityType = "capacity";
    private const string RegionType = "region";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    private readonly List<Rule> _rules = new();

    public IReadOnlyList<Rule> Rules => _rules;

    /// <summary>读取规则文件；文件不存在时返回空文档，损坏时返回空文档并给出提示。</summary>
    public static RuleDocument LoadOrCreate(string path, out string? loadMessage)
    {
        ArgumentNullException.ThrowIfNull(path);

        loadMessage = null;
        if (!File.Exists(path))
            return new RuleDocument();

        try
        {
            StoredDocument? stored = JsonSerializer.Deserialize<StoredDocument>(File.ReadAllText(path), SerializerOptions);
            if (stored is null)
                throw new InvalidDataException(RulesStrings.RuleFileEmpty);
            if (stored.Rules is null)
                throw new InvalidDataException(RulesStrings.RuleFileMissingList);

            List<StoredRule> items = stored.Rules;
            var document = new RuleDocument();
            var ids = new HashSet<RuleId>();
            foreach (StoredRule item in items)
            {
                Rule rule = FromStored(item);
                if (!ids.Add(rule.Id))
                    throw new InvalidDataException(string.Format(RulesStrings.DuplicateRuleIdFormat, rule.Id.value));
                document._rules.Add(rule);
            }
            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            loadMessage = string.Format(RulesStrings.RuleLoadFailedFormat, exception.Message);
            return new RuleDocument();
        }
    }

    /// <summary>按标识替换已有规则（保持原位置），不存在则追加到末尾。</summary>
    public void Upsert(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        for (int index = 0; index < _rules.Count; index++)
        {
            if (_rules[index].Id != rule.Id)
                continue;
            _rules[index] = rule;
            return;
        }
        _rules.Add(rule);
    }

    /// <summary>删除指定标识的规则；不存在时什么也不做。</summary>
    public void Remove(RuleId id)
    {
        for (int index = 0; index < _rules.Count; index++)
        {
            if (_rules[index].Id != id)
                continue;
            _rules.RemoveAt(index);
            return;
        }
    }

    /// <summary>清空所有已保存的规则。</summary>
    public void Clear() => _rules.Clear();

    /// <summary>写规则文件；先写同目录临时文件再整体替换，避免写入中断留下半截文件。</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        using (Stream stream = File.Create(temporaryPath))
            WriteJson(stream, _rules);
        File.Move(temporaryPath, path, true);
    }

    internal static void WriteJson(Stream stream, IEnumerable<Rule> rules)
    {
        var stored = new StoredDocument(rules.Select(ToStored).ToList());
        JsonSerializer.Serialize(stream, stored, SerializerOptions);
    }

    private static StoredRule ToStored(Rule rule) => rule switch
    {
        RelationRule relation => new StoredRule(
            RelationType,
            relation.Id.value,
            relation.DisplayName,
            new StoredRelation(
                relation.A.Value,
                relation.B.Value,
                relation.Kind,
                relation.Polarity,
                relation.Metric,
                relation.Distance,
                relation.Adjacency,
                relation.OffsetDx,
                relation.OffsetDy,
                relation.AllowNarrowAisle),
            null,
            null,
            null),
        PinnedRule pinned => new StoredRule(
            PinnedType,
            pinned.Id.value,
            pinned.DisplayName,
            null,
            new StoredPinned(pinned.Person.Value, pinned.Pos.Row, pinned.Pos.Col),
            null,
            null),
        CapacityRule capacity => new StoredRule(
            CapacityType,
            capacity.Id.value,
            capacity.DisplayName,
            null,
            null,
            new StoredCapacity(capacity.Axis.Type, capacity.Axis.Index, capacity.Min, capacity.Max),
            null),
        RegionRule region => new StoredRule(
            RegionType,
            region.Id.value,
            region.DisplayName,
            null,
            null,
            null,
            new StoredRegion(
                region.Person.Value,
                region.Polarity,
                region.RowMin,
                region.RowMax,
                region.ColMin,
                region.ColMax)),
        _ => throw new NotSupportedException(string.Format(RulesStrings.UnsupportedRuleTypeFormat, rule.GetType().Name)),
    };

    private static Rule FromStored(StoredRule stored)
    {
        if (stored.Id == Guid.Empty)
            throw new InvalidDataException(RulesStrings.MissingRuleId);

        var id = new RuleId(stored.Id);
        string displayName = stored.DisplayName ?? string.Empty;

        switch ((stored.Type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case RelationType:
            {
                StoredRelation relation = stored.Relation ?? throw new InvalidDataException(RulesStrings.MissingRelationData);
                if (relation.A == Guid.Empty || relation.B == Guid.Empty)
                    throw new InvalidDataException(string.Format(RulesStrings.MissingRelationPersonFormat, id.value));
                return new RelationRule(
                    id,
                    displayName,
                    new PersonId(relation.A),
                    new PersonId(relation.B),
                    relation.Kind,
                    relation.Polarity,
                    relation.Metric,
                    relation.Distance,
                    relation.OffsetDx,
                    relation.OffsetDy,
                    relation.Adjacency ?? (relation.Metric == DistanceMetric.Chebyshev ? AdjacencyMode.EightWay : AdjacencyMode.FourWay),
                    relation.AllowNarrowAisle ?? true);
            }
            case PinnedType:
            {
                StoredPinned pinned = stored.Pinned ?? throw new InvalidDataException(RulesStrings.MissingPinnedPosition);
                if (pinned.Person == Guid.Empty)
                    throw new InvalidDataException(string.Format(RulesStrings.MissingPinnedPersonFormat, id.value));
                return new PinnedRule(id, displayName, new PersonId(pinned.Person), new GridPos(pinned.Row, pinned.Col));
            }
            case CapacityType:
            {
                StoredCapacity capacity = stored.Capacity ?? throw new InvalidDataException(RulesStrings.MissingCapacityLimit);
                return new CapacityRule(id, displayName, new Axis(capacity.Type, capacity.Index), capacity.Min, capacity.Max);
            }
            case RegionType:
            {
                StoredRegion region = stored.Region ?? throw new InvalidDataException(RulesStrings.MissingRegionData);
                if (region.Person == Guid.Empty)
                    throw new InvalidDataException(string.Format(RulesStrings.MissingRegionPersonFormat, id.value));
                return new RegionRule(
                    id,
                    displayName,
                    new PersonId(region.Person),
                    region.Polarity,
                    region.RowMin,
                    region.RowMax,
                    region.ColMin,
                    region.ColMax);
            }
            default:
                throw new InvalidDataException(string.Format(RulesStrings.UnknownRuleTypeFormat, stored.Type));
        }
    }

    private sealed record StoredDocument(List<StoredRule>? Rules);

    private sealed record StoredRule(
        string? Type,
        Guid Id,
        string? DisplayName,
        StoredRelation? Relation,
        StoredPinned? Pinned,
        StoredCapacity? Capacity,
        StoredRegion? Region);

    private sealed record StoredRelation(
        Guid A,
        Guid B,
        RelationKind Kind,
        RulePolarity Polarity,
        DistanceMetric Metric,
        int? Distance,
        AdjacencyMode? Adjacency = null,
        int OffsetDx = 0,
        int OffsetDy = 0,
        bool? AllowNarrowAisle = null);

    private sealed record StoredPinned(Guid Person, int Row, int Col);

    private sealed record StoredCapacity(AxisType Type, int Index, int? Min, int? Max);

    private sealed record StoredRegion(
        Guid Person,
        RulePolarity Polarity,
        int? RowMin,
        int? RowMax,
        int? ColMin,
        int? ColMax);
}
