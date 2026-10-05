using System.Reflection;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Plugins;

/// <summary>
/// One workspace contributed by a plugin. The workspace instance is created and owned by the
/// application's workspace manager through <see cref="Create"/>; this catalog never instantiates
/// or disposes workspaces.
/// </summary>
internal sealed record WorkspaceRegistration(string Id, string DisplayName, string PluginId, Func<IWorkspace> Create);

/// <summary>
/// Discovers plugin manifests under the built-in and external plugin roots, initializes the
/// plugins in dependency order and exposes their contributions.
/// </summary>
/// <remarks>
/// <para>
/// Everything runs on the UI thread: discovery and initialization happen in the constructor,
/// plugins may only register contributions while <see cref="IUiPlugin.Initialize"/> runs, and the
/// contribution lists are immutable snapshots taken once initialization has finished.
/// </para>
/// <para>
/// Each plugin gets its own load context (dependency-resolution isolation, not a security
/// sandbox). Load contexts are never unloaded, so installing, updating or removing a plugin
/// requires an application restart.
/// </para>
/// <para>
/// A broken plugin never prevents independent plugins from loading: manifest, dependency and
/// initialization failures become diagnostics (the <see cref="Diagnostics"/> list plus stderr)
/// and only the failing plugin and its dependents are skipped. Scan order is deterministic — the
/// built-in root first, then the external root, each scanning child directories in ordinal order —
/// so duplicate plugin ids are diagnosed instead of silently resolved by filesystem order (the
/// first manifest in scan order owns the id).
/// </para>
/// </remarks>
internal sealed class PluginCatalog : IWorkspaceContext, IDisposable
{
    private const string ManifestFileName = "plugin.json";
    private const int MaxDependencyDepth = 256;

    private readonly string _dataDirectory;
    private readonly List<PluginCandidate> _discoveryOrder = new();
    private readonly Dictionary<string, PluginCandidate> _candidatesById = new(StringComparer.Ordinal);
    private readonly List<PluginCandidate> _initializedPlugins = new();
    private readonly List<string> _diagnostics = new();
    private readonly Dictionary<string, string> _workspaceOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _viewOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _commandOwners = new(StringComparer.Ordinal);
    private readonly List<WorkspaceRegistration> _workspaceRegistrations = new();
    private readonly List<(string Id, IUiView View)> _viewRegistrations = new();
    private readonly List<(string Id, IUiCommand Command)> _commandRegistrations = new();

    private IReadOnlyList<WorkspaceRegistration> _workspaces = Array.Empty<WorkspaceRegistration>();
    private IReadOnlyList<IUiView> _views = Array.Empty<IUiView>();
    private IReadOnlyList<IUiCommand> _commands = Array.Empty<IUiCommand>();
    private IReadOnlyList<PluginFontRequest> _fontRequests = Array.Empty<PluginFontRequest>();
    private bool _disposed;

    internal PluginCatalog(string builtinsDirectory, string pluginsDirectory, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(builtinsDirectory))
            throw new ArgumentException("A built-in plugins directory is required.", nameof(builtinsDirectory));
        if (string.IsNullOrWhiteSpace(pluginsDirectory))
            throw new ArgumentException("An external plugins directory is required.", nameof(pluginsDirectory));
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A plugin data directory is required.", nameof(dataDirectory));

        _dataDirectory = Path.GetFullPath(dataDirectory);

        ScanRoot(Path.GetFullPath(builtinsDirectory));
        ScanRoot(Path.GetFullPath(pluginsDirectory));
        InitializeDiscoveredPlugins();
        SnapshotContributions();
        SnapshotFontRequests();
    }

    /// <summary>Workspaces from successfully initialized plugins, ordered by display name.</summary>
    public IReadOnlyList<WorkspaceRegistration> Workspaces => _workspaces;

    /// <summary>Views from successfully initialized plugins, ordered by id (ordinal).</summary>
    public IReadOnlyList<IUiView> Views => _views;

    /// <summary>Commands from successfully initialized plugins, ordered by id (ordinal).</summary>
    public IReadOnlyList<IUiCommand> Commands => _commands;

    /// <summary>Human-readable load diagnostics; every entry has already been written to stderr.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Font requests from validated manifests.</summary>
    public IReadOnlyList<PluginFontRequest> FontRequests => _fontRequests;

    /// <summary>Disposes successfully initialized plugins in reverse initialization order.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var index = _initializedPlugins.Count - 1; index >= 0; index--)
        {
            var candidate = _initializedPlugins[index];
            if (candidate.Plugin is not { } plugin) continue;

            try
            {
                plugin.Dispose();
            }
            catch (Exception exception)
            {
                Report($"plugin '{candidate.Id}': Dispose threw {PluginDiagnostics.Describe(exception)}");
            }
        }

        _initializedPlugins.Clear();
        GC.SuppressFinalize(this);
    }

    // ---- discovery ---------------------------------------------------------------------------

    private void ScanRoot(string root)
    {
        // Both roots are optional; an empty plugin set is valid.
        if (!Directory.Exists(root)) return;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception exception)
        {
            Report($"cannot enumerate plugin directory '{root}': {PluginDiagnostics.Describe(exception)}");
            return;
        }

        Array.Sort(directories, StringComparer.Ordinal); // deterministic scan order
        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;

            // One unreadable or hostile manifest must never stop the remaining plugins.
            try
            {
                RegisterCandidate(directory, manifestPath);
            }
            catch (Exception exception)
            {
                Report($"plugin at '{manifestPath}' could not be inspected: {PluginDiagnostics.Describe(exception)}");
            }
        }
    }

    private void RegisterCandidate(string directory, string manifestPath)
    {
        var result = PluginManifestParser.Parse(manifestPath, directory);
        if (!result.Success)
            Report($"plugin at '{manifestPath}' is invalid: {result.Error}");

        var declaredId = result.DeclaredId;
        if (declaredId is not null && _candidatesById.TryGetValue(declaredId, out var existing))
        {
            // The first manifest in scan order owns an id. An invalid manifest never blocks a
            // valid one, so a later valid candidate supersedes an earlier invalid one.
            if (result.Success && existing.Status == CandidateStatus.Failed)
            {
                Report($"plugin '{declaredId}' at '{manifestPath}' replaces the invalid manifest at '{existing.ManifestPath}'");
                _candidatesById.Remove(declaredId);
            }
            else
            {
                Report($"plugin '{declaredId}' at '{manifestPath}' ignored: duplicate plugin id (already declared by '{existing.ManifestPath}')");
                return;
            }
        }

        if (!result.Success)
        {
            if (declaredId is not null)
                _candidatesById[declaredId] = PluginCandidate.Invalid(declaredId, manifestPath);
            return;
        }

        var manifest = result.Manifest!;
        var candidate = PluginCandidate.ForManifest(
            manifest,
            manifestPath,
            PluginManifestParser.ResolveAssemblyPath(directory, manifest.Assembly));
        _candidatesById.Add(manifest.Id, candidate);
        _discoveryOrder.Add(candidate);
    }

    // ---- initialization ----------------------------------------------------------------------

    private void InitializeDiscoveredPlugins()
    {
        var stack = new List<PluginCandidate>();
        foreach (var candidate in _discoveryOrder)
            VisitCandidate(candidate, stack);
    }

    private void VisitCandidate(PluginCandidate candidate, List<PluginCandidate> stack)
    {
        switch (candidate.Status)
        {
            case CandidateStatus.Loaded:
            case CandidateStatus.Failed:
                return;
            case CandidateStatus.Visiting:
                ReportCycle(candidate, stack);
                return;
        }

        if (stack.Count >= MaxDependencyDepth)
        {
            Report($"plugin '{candidate.Id}': dependency chain is deeper than {MaxDependencyDepth} plugins; the plugin will not be initialized");
            candidate.Status = CandidateStatus.Failed;
            return;
        }

        candidate.Status = CandidateStatus.Visiting;
        stack.Add(candidate);

        var dependenciesResolved = true;
        foreach (var dependencyId in candidate.Manifest!.Dependencies)
        {
            if (!_candidatesById.TryGetValue(dependencyId, out var dependency))
            {
                Report($"plugin '{candidate.Id}': required dependency '{dependencyId}' was not found");
                dependenciesResolved = false;
                continue;
            }

            VisitCandidate(dependency, stack);
            if (dependency.Status == CandidateStatus.Loaded) continue;

            // A plugin inside the dependency cycle was already reported by ReportCycle.
            if (!candidate.FailedInCycle)
                Report($"plugin '{candidate.Id}': required dependency '{dependencyId}' failed to load; the plugin will not be initialized");
            dependenciesResolved = false;
        }

        stack.RemoveAt(stack.Count - 1);

        if (!dependenciesResolved)
        {
            candidate.Status = CandidateStatus.Failed;
            return;
        }

        if (TryInitializePlugin(candidate))
        {
            candidate.Status = CandidateStatus.Loaded;
            _initializedPlugins.Add(candidate);
        }
        else
        {
            candidate.Status = CandidateStatus.Failed;
        }
    }

    private void ReportCycle(PluginCandidate entry, List<PluginCandidate> stack)
    {
        // A Visiting candidate is always on the stack, so the cycle is the stack suffix
        // that starts at the entry, closed by the entry itself.
        var cycleStart = stack.IndexOf(entry);
        var members = new List<PluginCandidate>();
        for (var index = cycleStart; index < stack.Count; index++)
            members.Add(stack[index]);

        foreach (var member in members)
        {
            member.Status = CandidateStatus.Failed;
            member.FailedInCycle = true;
        }

        Report($"plugin dependency cycle: {string.Join(" -> ", members.Select(member => member.Id))} -> {entry.Id}; none of the involved plugins will be initialized");
    }

    private bool TryInitializePlugin(PluginCandidate candidate)
    {
        var manifest = candidate.Manifest!;
        var assemblyPath = candidate.AssemblyPath!;

        string dataDirectory;
        try
        {
            // The manifest parser guarantees the id is a safe single path segment.
            dataDirectory = Path.Combine(_dataDirectory, manifest.Id);
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception exception)
        {
            Report($"plugin '{manifest.Id}': cannot create its data directory: {PluginDiagnostics.Describe(exception)}");
            return false;
        }

        IUiPlugin? plugin = null;
        try
        {
            var loadContext = new PluginLoadContext(manifest.Id, assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            var entryType = FindEntryType(assembly, manifest, out var problem);
            if (entryType is null)
            {
                Report($"plugin '{manifest.Id}': {problem}");
                return false;
            }

            plugin = (IUiPlugin)Activator.CreateInstance(entryType)!;
            var context = new PluginRegistrationContext(this, manifest.Id, dataDirectory);

            // Contributions are staged in the context and committed only after Initialize
            // returns, so a failed initialization leaves no orphan contributions.
            try
            {
                plugin.Initialize(context);
            }
            finally
            {
                context.Close();
            }
            CommitContributions(context);

            candidate.Plugin = plugin;
            candidate.LoadContext = loadContext;
            return true;
        }
        catch (Exception exception)
        {
            Report($"plugin '{manifest.Id}': initialization failed: {PluginDiagnostics.Describe(exception)}");
            DisposePluginQuietly(manifest.Id, plugin);
            return false;
        }
    }

    private static Type? FindEntryType(Assembly assembly, PluginManifest manifest, out string? problem)
    {
        Type? type;
        try
        {
            type = assembly.GetType(manifest.EntryPoint, throwOnError: false, ignoreCase: false);
        }
        catch (Exception exception)
        {
            problem = $"cannot resolve entry point type '{manifest.EntryPoint}': {PluginDiagnostics.Describe(exception)}";
            return null;
        }

        if (type is null)
        {
            problem = $"entry point type '{manifest.EntryPoint}' was not found in '{manifest.Assembly}'";
            return null;
        }
        if (type.IsNested || !type.IsPublic)
        {
            problem = $"entry point type '{manifest.EntryPoint}' must be a public top-level type";
            return null;
        }
        if (!type.IsClass || type.IsAbstract)
        {
            problem = $"entry point type '{manifest.EntryPoint}' must be a concrete (non-abstract) class";
            return null;
        }
        if (!typeof(IUiPlugin).IsAssignableFrom(type))
        {
            problem = $"entry point type '{manifest.EntryPoint}' does not implement IUiPlugin (plugins must use the host's PokeSeats.UI.PluginContracts assembly)";
            return null;
        }
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            problem = $"entry point type '{manifest.EntryPoint}' must declare a public parameterless constructor";
            return null;
        }

        problem = null;
        return type;
    }

    private void CommitContributions(PluginRegistrationContext context)
    {
        // The context already validated uniqueness, so these adds cannot fail; keeping the
        // validation at registration time is what makes the commit atomic per plugin.
        foreach (var workspace in context.Workspaces)
        {
            _workspaceRegistrations.Add(workspace);
            _workspaceOwners.Add(workspace.Id, context.PluginId);
        }

        foreach (var (id, view) in context.Views)
        {
            _viewRegistrations.Add((id, view));
            _viewOwners.Add(id, context.PluginId);
        }

        foreach (var (id, command) in context.Commands)
        {
            _commandRegistrations.Add((id, command));
            _commandOwners.Add(id, context.PluginId);
        }
    }

    private void DisposePluginQuietly(string pluginId, IUiPlugin? plugin)
    {
        if (plugin is null) return;
        try
        {
            plugin.Dispose();
        }
        catch (Exception exception)
        {
            Report($"plugin '{pluginId}': Dispose after failed initialization threw {PluginDiagnostics.Describe(exception)}");
        }
    }

    // ---- snapshots ---------------------------------------------------------------------------

    private void SnapshotContributions()
    {
        _workspaces = _workspaceRegistrations
            .OrderBy(workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(workspace => workspace.Id, StringComparer.Ordinal)
            .ToArray();
        _views = _viewRegistrations
            .OrderBy(view => view.Id, StringComparer.Ordinal)
            .Select(view => view.View)
            .ToArray();
        _commands = _commandRegistrations
            .OrderBy(command => command.Id, StringComparer.Ordinal)
            .Select(command => command.Command)
            .ToArray();
    }

    private void SnapshotFontRequests()
    {
        var requests = new List<PluginFontRequest>();
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _discoveryOrder)
        {
            if (candidate.Manifest is not { } manifest) continue;
            foreach (var request in manifest.Fonts)
            {
                if (families.Add(request.Family))
                    requests.Add(request);
            }
        }

        _fontRequests = requests;
    }

    private void Report(string message)
    {
        _diagnostics.Add(message);
        Console.Error.WriteLine($"[PluginCatalog] {message}");
    }

    // ---- nested types ------------------------------------------------------------------------

    private enum CandidateStatus
    {
        Pending,
        Visiting,
        Failed,
        Loaded
    }

    private sealed class PluginCandidate
    {
        private PluginCandidate(string id, string manifestPath, PluginManifest? manifest, string? assemblyPath)
        {
            Id = id;
            ManifestPath = manifestPath;
            Manifest = manifest;
            AssemblyPath = assemblyPath;
        }

        internal string Id { get; }
        internal string ManifestPath { get; }
        internal PluginManifest? Manifest { get; }
        internal string? AssemblyPath { get; }
        internal CandidateStatus Status { get; set; }
        internal bool FailedInCycle { get; set; }
        internal IUiPlugin? Plugin { get; set; }

        /// <summary>Keeps the load context alive for the lifetime of the catalog; contexts are never unloaded.</summary>
        internal PluginLoadContext? LoadContext { get; set; }

        internal static PluginCandidate ForManifest(PluginManifest manifest, string manifestPath, string assemblyPath)
            => new(manifest.Id, manifestPath, manifest, assemblyPath);

        internal static PluginCandidate Invalid(string declaredId, string manifestPath)
            => new(declaredId, manifestPath, null, null) { Status = CandidateStatus.Failed };
    }

    private sealed class PluginRegistrationContext : IPluginContext
    {
        private readonly PluginCatalog _catalog;
        private readonly HashSet<string> _workspaceIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _viewIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _commandIds = new(StringComparer.Ordinal);
        private bool _closed;

        internal PluginRegistrationContext(PluginCatalog catalog, string pluginId, string dataDirectory)
        {
            _catalog = catalog;
            PluginId = pluginId;
            DataDirectory = dataDirectory;
        }

        public string PluginId { get; }

        public string DataDirectory { get; }

        internal List<WorkspaceRegistration> Workspaces { get; } = new();

        internal List<(string Id, IUiView View)> Views { get; } = new();

        internal List<(string Id, IUiCommand Command)> Commands { get; } = new();

        public void RegisterWorkspace(string id, string displayName, Func<IWorkspace> create)
        {
            EnsureOpen();
            var contributionId = RequireId(id, "workspace");
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("The workspace display name must not be blank.", nameof(displayName));
            if (create is null)
                throw new ArgumentNullException(nameof(create), "the workspace factory must not be null");
            if (!_workspaceIds.Add(contributionId))
                throw new InvalidOperationException($"workspace id '{contributionId}' is registered twice by plugin '{PluginId}'");
            if (_catalog._workspaceOwners.TryGetValue(contributionId, out var owner))
                throw new InvalidOperationException($"workspace id '{contributionId}' is already provided by plugin '{owner}'");
            Workspaces.Add(new WorkspaceRegistration(contributionId, displayName, PluginId, create));
        }

        public void RegisterView(IUiView view)
        {
            EnsureOpen();
            if (view is null)
                throw new ArgumentNullException(nameof(view));
            var contributionId = RequireId(view.Id, "view");
            if (!_viewIds.Add(contributionId))
                throw new InvalidOperationException($"view id '{contributionId}' is registered twice by plugin '{PluginId}'");
            if (_catalog._viewOwners.TryGetValue(contributionId, out var owner))
                throw new InvalidOperationException($"view id '{contributionId}' is already provided by plugin '{owner}'");
            Views.Add((contributionId, view));
        }

        public void RegisterCommand(IUiCommand command)
        {
            EnsureOpen();
            if (command is null)
                throw new ArgumentNullException(nameof(command));
            var contributionId = RequireId(command.Id, "command");
            if (!_commandIds.Add(contributionId))
                throw new InvalidOperationException($"command id '{contributionId}' is registered twice by plugin '{PluginId}'");
            if (_catalog._commandOwners.TryGetValue(contributionId, out var owner))
                throw new InvalidOperationException($"command id '{contributionId}' is already provided by plugin '{owner}'");
            Commands.Add((contributionId, command));
        }

        internal void Close() => _closed = true;

        private void EnsureOpen()
        {
            if (_closed)
                throw new InvalidOperationException("contributions can only be registered while IUiPlugin.Initialize is running");
        }

        private static string RequireId(string? id, string category)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException($"the {category} contribution has a blank id");
            return id;
        }
    }
}
