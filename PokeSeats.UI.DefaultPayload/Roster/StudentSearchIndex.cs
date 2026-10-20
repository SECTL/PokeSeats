using System.Text;
using hyjiacan.py4n;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>
/// 学生名单的搜索索引：匹配姓名、学号、全拼与拼音首字母。
/// </summary>
/// <remarks>
/// <para>
/// 全拼与首字母由 hyjiacan.pinyin4net 生成；姓走姓氏库（含复姓与多音姓氏的姓氏读音），
/// 名字走单字库并取首读音。姓氏库中少数与姓氏习惯读音不一致的条目
/// （区、朴、秘、召、种、覃、折、长、员、繁、谌、句、洗、郄、郇）在首次使用前用
/// <see cref="Name4Net.UpdateMap"/> 覆盖。
/// </para>
/// <para>
/// 匹配不区分大小写，忽略空白、标点与全角差异之外的字符，并把 v 归一为 u
/// （吕的全拼 lv 与 lu 都能命中）。索引只在 <see cref="StudentRoster.Revision"/> 变化时重建，
/// <see cref="Find"/> 的结果按查询词缓存在同一名单版本内，可以随界面每帧调用。
/// </para>
/// <para>
/// 局限：基本区（U+4E00–U+9FBB）之外的生僻字（含扩展 A/B 区字符）不产生拼音，
/// 只保留在姓名原文里；名字中的多音字固定取词典首读音，不按名字语境消歧。
/// </para>
/// </remarks>
internal sealed class StudentSearchIndex
{
    /// <summary>姓氏库中最长的复姓长度（爱新觉罗）。</summary>
    private const int MaximumSurnameLength = 4;

    /// <summary>同一名单版本内缓存的查询词数量上限（两个人员选择器共用本索引）。</summary>
    private const int MaximumCachedQueries = 8;

    private const int RankNoMatch = -1;
    private const int RankNameEquals = 0;
    private const int RankNumberEquals = 1;
    private const int RankNameStarts = 2;
    private const int RankNumberStarts = 3;
    private const int RankInitialsStarts = 4;
    private const int RankPinyinStarts = 5;
    private const int RankNameContains = 6;
    private const int RankNumberContains = 7;
    private const int RankInitialsContains = 8;
    private const int RankPinyinContains = 9;

    private static readonly PinyinFormat PinyinStyle =
        PinyinFormat.WITHOUT_TONE | PinyinFormat.LOWERCASE | PinyinFormat.WITH_V;

    /// <summary>姓氏库读音修正与补充；每个姓只保留一个读音，多音姓取常用姓氏读音。</summary>
    private static readonly Dictionary<string, string[]> SurnameReadings =
        new()
        {
            ["区"] = new[] { "ou1" },
            ["朴"] = new[] { "piao2" },
            ["秘"] = new[] { "bi4" },
            ["召"] = new[] { "shao4" },
            ["种"] = new[] { "chong2" },
            ["覃"] = new[] { "qin2" },
            ["折"] = new[] { "she2" },
            ["长"] = new[] { "chang2" },
            ["员"] = new[] { "yun4" },
            ["繁"] = new[] { "po2" },
            ["谌"] = new[] { "chen2" },
            ["句"] = new[] { "gou1" },
            ["洗"] = new[] { "xian3" },
            ["郄"] = new[] { "qie4" },
            ["郇"] = new[] { "huan2" },
        };

    private static bool _surnameReadingsRegistered;

    private readonly StudentRoster _roster;
    private readonly Dictionary<PersonId, Person> _people = new();
    private readonly List<SearchEntry> _entries = new();
    private readonly Dictionary<string, List<Person>> _cache = new();
    private int _peopleRevision = -1;
    private int _entriesRevision = -1;
    private int _cacheRevision = -1;

    public StudentSearchIndex(StudentRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        _roster = roster;
    }

    /// <summary>
    /// 按姓名、学号、全拼、首字母匹配查询词，结果按匹配优先级和名单顺序排列。
    /// </summary>
    /// <remarks>
    /// 查询词按空白切分，每个片段都必须命中至少一个字段；空白查询返回全部学生。
    /// 结果按查询词缓存在当前名单版本内（最多 <see cref="MaximumCachedQueries"/> 条），
    /// 名单版本变化时清空；返回的列表归索引所有，调用方不要修改。
    /// </remarks>
    public IEnumerable<Person> Find(string? query)
    {
        RefreshPeople();
        RefreshEntries();

        if (_cacheRevision != _roster.Revision)
        {
            _cache.Clear();
            _cacheRevision = _roster.Revision;
        }

        string key = query ?? string.Empty;
        if (_cache.TryGetValue(key, out var cached) && cached is not null)
            return cached;

        if (_cache.Count >= MaximumCachedQueries)
            _cache.Clear();

        List<Person> result = Search(key);
        _cache.Add(key, result);
        return result;
    }

    private List<Person> Search(string query)
    {
        List<string> tokens = Tokenize(query);
        var rank = new List<SearchHit>(_entries.Count);
        if (tokens.Count > 0)
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                int score = Rank(_entries[index], tokens);
                if (score != RankNoMatch)
                    rank.Add(new SearchHit(score, index));
            }

            rank.Sort(static (left, right) =>
            {
                int byRank = left.Rank.CompareTo(right.Rank);
                return byRank != 0 ? byRank : left.Index.CompareTo(right.Index);
            });
        }
        else
        {
            for (int index = 0; index < _entries.Count; index++)
                rank.Add(new SearchHit(RankNameEquals, index));
        }

        var result = new List<Person>(rank.Count);
        foreach (SearchHit hit in rank)
            result.Add(_entries[hit.Index].Person);
        return result;
    }

    /// <summary>按人员标识取名单中的学生；名单里没有该标识时返回 null。</summary>
    public Person? Get(PersonId id)
    {
        RefreshPeople();
        return _people.TryGetValue(id, out var person) ? person : null;
    }

    private void RefreshPeople()
    {
        if (_peopleRevision == _roster.Revision)
            return;

        _people.Clear();
        foreach (Person person in _roster.Students)
            _people[person.Id] = person;
        _peopleRevision = _roster.Revision;
    }

    private void RefreshEntries()
    {
        if (_entriesRevision == _roster.Revision)
            return;

        EnsureSurnameReadings();
        _entries.Clear();
        foreach (Person person in _roster.Students)
            _entries.Add(CreateEntry(person));
        _entriesRevision = _roster.Revision;
    }

    private static SearchEntry CreateEntry(Person person)
    {
        var full = new StringBuilder();
        var initials = new StringBuilder();
        AppendNamePinyin(person.Name, full, initials);

        return new SearchEntry(
            person,
            Fold(person.Name),
            Fold(person.StudentNumber),
            Fold(full.ToString()),
            Fold(initials.ToString()));
    }

    /// <summary>姓取姓氏读音，名字逐字取首读音；非汉字字母数字原样保留。</summary>
    private static void AppendNamePinyin(string name, StringBuilder full, StringBuilder initials)
    {
        int index = 0;
        bool atWordStart = true;
        string? surname = ResolveSurname(name);
        if (surname is not null)
        {
            AppendSurnamePinyin(surname, full, initials);
            index = surname.Length;
            atWordStart = true;
        }

        for (; index < name.Length; index++)
        {
            char character = name[index];
            if (PinyinUtil.IsHanzi(character))
            {
                string pinyin = HanziPinyin(character);
                if (pinyin.Length > 0)
                {
                    full.Append(pinyin); // 词典里没有的生僻字：不进拼音字段，仍可用汉字原文命中。
                    initials.Append(pinyin[0]);
                }
                atWordStart = true;
            }
            else if (char.IsLetterOrDigit(character))
            {
                full.Append(character);
                if (atWordStart)
                    initials.Append(character); // 拼音名 / 英文名按词首字母取（Alice Wang → aw）。
                atWordStart = false;
            }
            else
            {
                atWordStart = true;
            }
        }
    }

    /// <summary>取姓名开头最长的姓氏库条目（复姓优先，最长 4 字）；没有则返回 null。</summary>
    private static string? ResolveSurname(string name)
    {
        int maximum = Math.Min(name.Length, MaximumSurnameLength);
        for (int length = maximum; length >= 1; length--)
        {
            string candidate = name[..length];
            if (candidate.All(PinyinUtil.IsHanzi) && Name4Net.GetPinyin(candidate) is not null)
                return candidate;
        }
        return null;
    }

    private static void AppendSurnamePinyin(string surname, StringBuilder full, StringBuilder initials)
    {
        string? pinyin = Name4Net.GetPinyin(surname, PinyinStyle);
        if (string.IsNullOrEmpty(pinyin))
            return;

        foreach (string syllable in pinyin.Split(' '))
        {
            if (syllable.Length == 0)
                continue;
            full.Append(syllable);
            initials.Append(syllable[0]);
        }
    }

    private static string HanziPinyin(char character)
    {
        string[] readings = Pinyin4Net.GetPinyin(character, PinyinStyle);
        return readings.Length > 0 ? readings[0] : string.Empty;
    }

    private static List<string> Tokenize(string? query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return tokens;

        foreach (string part in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = Fold(part);
            if (token.Length > 0)
                tokens.Add(token);
        }
        return tokens;
    }

    private static int Rank(SearchEntry entry, List<string> tokens)
    {
        int worst = RankNameEquals;
        foreach (string token in tokens)
        {
            int score = RankToken(entry, token);
            if (score == RankNoMatch)
                return RankNoMatch;
            if (score > worst)
                worst = score;
        }
        return worst;
    }

    private static int RankToken(SearchEntry entry, string token)
    {
        if (entry.Name == token)
            return RankNameEquals;
        if (entry.Number.Length > 0 && entry.Number == token)
            return RankNumberEquals;
        if (entry.Name.StartsWith(token, StringComparison.Ordinal))
            return RankNameStarts;
        if (entry.Number.StartsWith(token, StringComparison.Ordinal))
            return RankNumberStarts;
        if (entry.Initials.StartsWith(token, StringComparison.Ordinal))
            return RankInitialsStarts;
        if (entry.FullPinyin.StartsWith(token, StringComparison.Ordinal))
            return RankPinyinStarts;
        if (entry.Name.Contains(token, StringComparison.Ordinal))
            return RankNameContains;
        if (entry.Number.Contains(token, StringComparison.Ordinal))
            return RankNumberContains;
        if (entry.Initials.Contains(token, StringComparison.Ordinal))
            return RankInitialsContains;
        if (entry.FullPinyin.Contains(token, StringComparison.Ordinal))
            return RankPinyinContains;
        return RankNoMatch;
    }

    /// <summary>搜索用归一化：小写、只保留字母数字（含汉字）、v 归一为 u。</summary>
    private static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character))
                continue;
            char lower = char.ToLowerInvariant(character);
            builder.Append(lower == 'v' ? 'u' : lower);
        }
        return builder.ToString();
    }

    /// <summary>把姓氏读音修正登记到姓氏库；只执行一次，首次建索引前调用。</summary>
    private static void EnsureSurnameReadings()
    {
        if (_surnameReadingsRegistered)
            return;

        Name4Net.UpdateMap(SurnameReadings, replace: true);
        _surnameReadingsRegistered = true;
    }

    private sealed record SearchEntry(Person Person, string Name, string Number, string FullPinyin, string Initials);

    private readonly record struct SearchHit(int Rank, int Index);
}
