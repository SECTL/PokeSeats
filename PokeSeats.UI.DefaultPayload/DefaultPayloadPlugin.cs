using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

public sealed class DefaultPayloadPlugin : IUiPlugin
{
    private StudentRoster? _roster;
    private SeatingSession? seating;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var roster = new StudentRoster();
        roster.Load(Path.Combine(context.DataDirectory, "students.json"));
        _roster = roster;

        string layoutPath = Path.Combine(context.DataDirectory, "layout.json");
        var layout = LayoutDocument.LoadOrCreate(layoutPath, out string? loadMessage);
        context.RegisterView(new RosterPanel(roster, context.DataDirectory));
        context.RegisterView(new LayoutEditorView(layout, layoutPath, loadMessage));
        string rulesPath = Path.Combine(context.DataDirectory, "rules.json");
        var rules = RuleDocument.LoadOrCreate(rulesPath, out string? rulesMessage);
        seating = new SeatingSession(roster, layout, rules, context.Events);
        context.RegisterView(new RuleEditorView(roster, layout, rules, rulesPath, rulesMessage, seating));
        context.RegisterView(new SeatingResultView(seating, context.DataDirectory));
        context.RegisterCommand(seating);
    }

    public void Dispose()
    {
        seating?.Dispose();
        seating = null;
        _roster = null;
    }
}
