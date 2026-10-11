using PokeSeats.Core.Solver;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>UI-thread state; the worker only reads the captured request and returns a result.</summary>
internal sealed class SeatingSession : IUiCommand, IDisposable
{
    private readonly StudentRoster roster;
    private readonly LayoutDocument layout;
    private readonly RuleDocument rules;
    private readonly IUiEventBus events;
    private Task<Outcome>? worker;
    private bool pending;
    private bool disposed;
    private int rosterRevision;
    private int layoutRevision;
    private Rule[] ruleSnapshot = Array.Empty<Rule>();
    private readonly HashSet<RuleId> conflicts = new();

    private sealed record Outcome(SolverResult? Result, string? Error);

    public SeatingSession(StudentRoster roster, LayoutDocument layout, RuleDocument rules, IUiEventBus events)
    {
        this.roster = roster; this.layout = layout; this.rules = rules; this.events = events;
    }

    public string Id => "pokeseats.defaultpayload.start-seating";
    public string DisplayName => "开始自动排座";
    public bool IsRunning => pending || worker is not null;
    public SolverRequest? Request { get; private set; }
    public SolverResult? Result { get; private set; }
    public string? Error { get; private set; }
    public int RunNumber { get; private set; }
    public bool IsCurrent => Request is not null && rosterRevision == roster.Revision
        && layoutRevision == layout.Revision && ruleSnapshot.SequenceEqual(rules.Rules);

    public void Execute()
    {
        if (disposed) return;
        Update();
        if (!IsRunning)
        {
            rosterRevision = roster.Revision; layoutRevision = layout.Revision;
            ruleSnapshot = rules.Rules.ToArray();
            Request = new SolverRequest(roster.Students.ToArray(), layout.ToGrid(), ruleSnapshot, SolverBackend.OrToolsCpSat);
            Result = null; Error = null; conflicts.Clear(); RunNumber++;
            pending = true;
        }
        events.Publish(new UiViewOpenRequested(SeatingResultView.ViewId));
    }

    /// <summary>Called after drawing the initial progress state so even a quick solve shows progress first.</summary>
    public void Launch()
    {
        if (!pending || disposed) return;
        SolverRequest request = Request!;
        pending = false;
        worker = Task.Run(() =>
        {
            try
            {
                return request.Roster.Count == 0
                    ? new Outcome(null, "学生花名册当前为空，请先在“学生名单”面板导入班级名单。")
                    : new Outcome(new SolverService().Solve(request), null);
            }
            catch (Exception exception)
            {
                return new Outcome(null, $"排座引擎执行异常：{exception.Message}");
            }
        });
    }

    public void Update()
    {
        if (disposed) return;
        if (!IsCurrent) conflicts.Clear();
        if (worker is not { IsCompleted: true }) return;
        Outcome outcome = worker.GetAwaiter().GetResult();
        worker = null;
        Result = outcome.Result; Error = outcome.Error;
        if (IsCurrent && Result is { IsSuccess: false })
        {
            conflicts.UnionWith(Result.ConflictingRuleIds);
            if (conflicts.Count > 0)
                events.Publish(new UiViewOpenRequested("pokeseats.defaultpayload.rules"));
        }
    }

    public bool IsConflicting(RuleId id) => IsCurrent && conflicts.Contains(id);
    public int ConflictCount => IsCurrent ? conflicts.Count : 0;

    public void Dispose()
    {
        disposed = true;
        pending = false;
        // The worker owns no UI state or event scope and disposes its native solver before returning.
        worker = null;
    }
}
