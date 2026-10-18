using System.Text.Json;
using System.Text.Json.Serialization;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>可编辑的排座目标集合，用显式 DTO 做 JSON 往返。</summary>
/// <remarks>
/// <para>
/// 每条目标以稳定的 <see cref="SeatObjectiveId"/> 标识，编辑既有目标时沿用原标识。
/// 保存先写同目录临时文件再整体替换，与 <see cref="RuleDocument"/> 一致。
/// </para>
/// <para>
/// 加载失败时不改动原文件，返回空文档并通过 <c>loadMessage</c> 报告原因；
/// 空文档、缺少目标列表、空条目、标识重复、标识或属性缺失、显示名称为空、
/// 未知形状或维度、以及 Centered/Bimodal 使用非中心距维度都按文件损坏处理。
/// 区域边界和形状契约在保存前验证；旧形状在加载时迁移为各排均衡。
/// </para>
/// </remarks>
internal sealed class ObjectiveDocument
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new ObjectiveShapeConverter(), new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    private readonly List<SeatObjective> _objectives = new();

    public IReadOnlyList<SeatObjective> Objectives => _objectives;

    /// <summary>每次实际修改或成功保存后自增，供排座会话检测过期快照。</summary>
    public int Revision { get; private set; }

    /// <summary>读取目标文件；文件不存在时返回空文档，损坏时返回空文档并给出提示。</summary>
    public static ObjectiveDocument LoadOrCreate(string path, out string? loadMessage)
    {
        ArgumentNullException.ThrowIfNull(path);

        loadMessage = null;
        if (!File.Exists(path))
            return new ObjectiveDocument();

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            StoredDocument? stored = json.RootElement.Deserialize<StoredDocument>(SerializerOptions);
            if (stored is null)
                throw new InvalidDataException(ObjectiveFileEmpty);
            if (stored.Objectives is null)
                throw new InvalidDataException(ObjectiveFileMissingList);

            var document = new ObjectiveDocument();
            var rawObjectives = json.RootElement.EnumerateObject()
                .Last(property => string.Equals(property.Name, "Objectives", StringComparison.OrdinalIgnoreCase))
                .Value.EnumerateArray().ToArray();
            int index = 0;
            foreach (StoredObjective item in stored.Objectives)
            {
                if (item is null)
                    throw new InvalidDataException(ObjectiveItemEmpty);
                bool legacyBalance = rawObjectives[index++].EnumerateObject().Any(property =>
                    string.Equals(property.Name, "Shape", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "GroupBalance");
                document._objectives.Add(FromStored(item, legacyBalance));
            }

            Validate(document._objectives);
            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            loadMessage = string.Format(PayloadStrings.ObjectiveLoadFailedFormat, exception.Message);
            return new ObjectiveDocument();
        }
    }

    /// <summary>按标识替换已有目标（保持原位置），不存在则追加到末尾。</summary>
    public void Upsert(SeatObjective objective)
    {
        ArgumentNullException.ThrowIfNull(objective);

        for (int index = 0; index < _objectives.Count; index++)
        {
            if (_objectives[index].Id != objective.Id)
                continue;
            _objectives[index] = objective;
            Revision++;
            return;
        }

        _objectives.Add(objective);
        Revision++;
    }

    /// <summary>删除指定标识的目标；不存在时什么也不做。</summary>
    public void Remove(SeatObjectiveId id)
    {
        for (int index = 0; index < _objectives.Count; index++)
        {
            if (_objectives[index].Id != id)
                continue;
            _objectives.RemoveAt(index);
            Revision++;
            return;
        }
    }

    /// <summary>将目标上移一位；已在首位或不存在时什么也不做。</summary>
    public void MoveUp(SeatObjectiveId id)
    {
        int index = IndexOf(id);
        if (index <= 0)
            return;

        (_objectives[index - 1], _objectives[index]) = (_objectives[index], _objectives[index - 1]);
        Revision++;
    }

    /// <summary>将目标下移一位；已在末位或不存在时什么也不做。</summary>
    public void MoveDown(SeatObjectiveId id)
    {
        int index = IndexOf(id);
        if (index < 0 || index >= _objectives.Count - 1)
            return;

        (_objectives[index], _objectives[index + 1]) = (_objectives[index + 1], _objectives[index]);
        Revision++;
    }

    /// <summary>写目标文件；先校验全部目标，再写同目录临时文件并整体替换。</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Validate(_objectives);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        using (Stream stream = File.Create(temporaryPath))
            WriteJson(stream, _objectives);
        File.Move(temporaryPath, path, true);
        Revision++;
    }

    /// <summary>序列化目标列表，供 ZIP 导出复用。</summary>
    internal static void WriteJson(Stream stream, IEnumerable<SeatObjective> objectives)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(objectives);

        var stored = new StoredDocument(CurrentVersion, objectives.Select(ToStored).ToList());
        JsonSerializer.Serialize(stream, stored, SerializerOptions);
    }

    private int IndexOf(SeatObjectiveId id)
    {
        for (int index = 0; index < _objectives.Count; index++)
        {
            if (_objectives[index].Id == id)
                return index;
        }
        return -1;
    }

    private static void Validate(IReadOnlyList<SeatObjective> objectives)
    {
        var ids = new HashSet<SeatObjectiveId>();
        foreach (SeatObjective objective in objectives)
        {
            if (objective is null)
                throw new InvalidDataException(ObjectiveItemEmpty);
            if (objective.Id.Value == Guid.Empty)
                throw new InvalidDataException(ObjectiveMissingId);
            if (!ids.Add(objective.Id))
                throw new InvalidDataException(string.Format(ObjectiveDuplicateIdFormat, objective.Id.Value));
            if (objective.Priority < 1)
                throw new InvalidDataException(PayloadStrings.ObjectivePriorityInvalid);
            if (string.IsNullOrWhiteSpace(objective.DisplayName))
                throw new InvalidDataException(ObjectiveMissingDisplayName);
            if (objective.AttributeId.Value == Guid.Empty)
                throw new InvalidDataException(ObjectiveMissingAttribute);

            if (!Enum.IsDefined(typeof(ObjectiveShape), objective.Shape))
                throw new InvalidDataException(string.Format(ObjectiveUnknownShapeFormat, objective.Shape));
            if (!Enum.IsDefined(typeof(SeatDimension), objective.Dimension))
                throw new InvalidDataException(string.Format(ObjectiveUnknownDimensionFormat, objective.Dimension));
            if (ShapeValidationError(objective) is { } error)
                throw new InvalidDataException(error);
        }
    }

    private static SeatObjective FromStored(StoredObjective stored, bool legacyBalance) => new(
        new SeatObjectiveId(stored.Id),
        stored.DisplayName ?? string.Empty,
        new PersonAttributeId(stored.AttributeId),
        legacyBalance ? SeatDimension.Row : stored.Dimension,
        stored.Shape,
        stored.Priority ?? 1,
        legacyBalance || stored.Region is null ? null : new SeatRegion(stored.Region.RowMin, stored.Region.RowMax, stored.Region.ColMin, stored.Region.ColMax),
        stored.RegionPreferenceDirection ?? RegionPreferenceDirection.HighValues);

    private static StoredObjective ToStored(SeatObjective objective) => new(
        objective.Id.Value,
        objective.DisplayName,
        objective.AttributeId.Value,
        objective.Dimension,
        objective.Shape,
        objective.Priority,
        objective.Region is { } region ? new StoredRegion(region.RowMin, region.RowMax, region.ColMin, region.ColMax) : null,
        objective.Shape == ObjectiveShape.RegionPreference ? objective.RegionPreferenceDirection : null);

    internal static string? ShapeValidationError(SeatObjective objective)
    {
        if (objective.Region is { } region && (region.RowMin < 0 || region.ColMin < 0
            || region.RowMax < region.RowMin || region.ColMax < region.ColMin))
            return PayloadStrings.ObjectiveRegionInvalid;
        if (objective.Shape == ObjectiveShape.RowBalance && (objective.Region is not null || objective.Dimension != SeatDimension.Row))
            return PayloadStrings.ObjectiveRowBalanceInvalid;
        if (objective.Shape == ObjectiveShape.RegionPreference && objective.Region is null)
            return PayloadStrings.ObjectiveRegionRequired;
        if (!Enum.IsDefined(typeof(RegionPreferenceDirection), objective.RegionPreferenceDirection))
            return PayloadStrings.ObjectiveDirectionInvalid;
        if (objective.Shape is ObjectiveShape.Centered or ObjectiveShape.Bimodal && objective.Dimension != SeatDimension.DistanceFromCenter)
            return PayloadStrings.ObjectiveInvalidShape;
        return null;
    }

    private const string ObjectiveFileEmpty = "排座目标文件内容为空。";
    private const string ObjectiveFileMissingList = "排座目标文件缺少目标列表。";
    private const string ObjectiveItemEmpty = "排座目标文件中包含空目标条目。";
    private const string ObjectiveMissingId = "排座目标缺少标识。";
    private const string ObjectiveDuplicateIdFormat = "排座目标标识重复：{0}。";
    private const string ObjectiveMissingDisplayName = "排座目标缺少显示名称。";
    private const string ObjectiveMissingAttribute = "排座目标缺少属性。";
    private const string ObjectiveUnknownShapeFormat = "排座目标包含未识别的映射形状：{0}。";
    private const string ObjectiveUnknownDimensionFormat = "排座目标包含未识别的座位维度：{0}。";

    private sealed record StoredDocument(int Version, List<StoredObjective>? Objectives);

    private sealed record StoredObjective(
        Guid Id,
        string? DisplayName,
        Guid AttributeId,
        SeatDimension Dimension,
        ObjectiveShape Shape,
        int? Priority,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StoredRegion? Region,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegionPreferenceDirection? RegionPreferenceDirection);

    private sealed record StoredRegion(int RowMin, int RowMax, int ColMin, int ColMax);

    private sealed class ObjectiveShapeConverter : JsonConverter<ObjectiveShape>
    {
        public override ObjectiveShape Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("目标形状必须为字符串。");
            string? value = reader.GetString();
            if (value == "GroupBalance") return ObjectiveShape.RowBalance;
            if (Enum.TryParse<ObjectiveShape>(value, ignoreCase: false, out var shape)
                && Enum.IsDefined(typeof(ObjectiveShape), shape) && shape.ToString() == value) return shape;
            throw new JsonException(string.Format(ObjectiveUnknownShapeFormat, value));
        }

        public override void Write(Utf8JsonWriter writer, ObjectiveShape value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
