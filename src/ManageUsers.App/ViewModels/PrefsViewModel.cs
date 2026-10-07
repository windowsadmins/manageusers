using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ManageUsers.Models;
using ManageUsers.Services;
using Microsoft.UI.Dispatching;

namespace ManageUsers.App.ViewModels;

/// <summary>
/// ViewModel for the Prefs tab. Shows every setting with the value a run would use and
/// where it comes from. Settings live in HKLM, so the tab is read-only unless this process
/// is elevated; Unlock relaunches the app elevated on this tab. When elevated, a changed
/// setting saves to machine settings (HKLM\SOFTWARE\ManageUsers\Settings) after a short
/// pause. Only settings changed here are written, so values that come from Config.yaml
/// stay there. A setting policy manages is locked and never written.
/// </summary>
public partial class PrefsViewModel : ObservableObject
{
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly HashSet<string> _dirty = [];
    private System.Threading.Timer? _autoSaveTimer;
    private bool _isLoading;

    public static readonly string[] Strategies = ["login_and_creation", "creation_only"];

    public PrefsViewModel()
    {
        Exclusions.CollectionChanged += (_, _) => MarkDirty(SettingsResolver.Exclusions);
        DeletableAdmins.CollectionChanged += (_, _) => MarkDirty(SettingsResolver.DeletableAdmins);
        EndOfTermDates.CollectionChanged += (_, _) => MarkDirty(SettingsResolver.EndOfTermDates);
        Rules.CollectionChanged += OnRulesChanged;
    }

    // ── Settings ─────────────────────────────────────────────────

    public ObservableCollection<string> Exclusions { get; } = [];
    public ObservableCollection<string> DeletableAdmins { get; } = [];
    public ObservableCollection<string> EndOfTermDates { get; } = [];
    public ObservableCollection<RuleItem> Rules { get; } = [];

    [ObservableProperty] private bool _deleteAdmins;
    [ObservableProperty] private double _defaultDurationDays = 28;
    [ObservableProperty] private string _defaultStrategy = "login_and_creation";
    [ObservableProperty] private bool _defaultForceAtEndOfTerm;
    [ObservableProperty] private string _inventoryPath = "";

    // Text boxes for adding list entries.
    [ObservableProperty] private string _newExclusion = "";
    [ObservableProperty] private string _newDeletableAdmin = "";
    [ObservableProperty] private string _newTermDate = "";
    [ObservableProperty] private string _termDateError = "";

    partial void OnDeleteAdminsChanged(bool value) => MarkDirty(SettingsResolver.DeleteAdmins);
    partial void OnDefaultDurationDaysChanged(double value) => MarkDirty(SettingsResolver.DefaultPolicyDurationDays);
    partial void OnDefaultStrategyChanged(string value)
    {
        OnPropertyChanged(nameof(DefaultStrategyIndex));
        MarkDirty(SettingsResolver.DefaultPolicyStrategy);
    }

    /// <summary>The strategy as a ComboBox index into <see cref="Strategies"/>.</summary>
    public int DefaultStrategyIndex
    {
        get => Math.Max(0, Array.IndexOf(Strategies, DefaultStrategy));
        set { if (value >= 0 && value < Strategies.Length) DefaultStrategy = Strategies[value]; }
    }
    partial void OnDefaultForceAtEndOfTermChanged(bool value) => MarkDirty(SettingsResolver.DefaultPolicyForceAtEndOfTerm);
    partial void OnInventoryPathChanged(string value) => MarkDirty(SettingsResolver.InventoryPath);
    partial void OnTermDateErrorChanged(string value) => OnPropertyChanged(nameof(HasTermDateError));

    public bool HasTermDateError => TermDateError.Length > 0;

    // ── Sources and management state ─────────────────────────────

    private Dictionary<string, SettingSource> _sources = [];
    private HashSet<string> _managedKeys = [];

    [ObservableProperty] private string _configFileWarning = "";
    public bool HasConfigFileWarning => ConfigFileWarning.Length > 0;
    partial void OnConfigFileWarningChanged(string value) => OnPropertyChanged(nameof(HasConfigFileWarning));

    private string SourceText(string key) => _managedKeys.Contains(key)
        ? "Managed by Policy"
        : _sources.TryGetValue(key, out var source) ? source switch
        {
            SettingSource.MachineSettings => "Set on this device",
            SettingSource.ConfigFile => "From Config.yaml",
            _ => "Default"
        } : "Default";

    private bool CanEdit(string key) => PrefsElevation.CanEdit(IsElevated, _managedKeys.Contains(key));

    public bool CanEditExclusions => CanEdit(SettingsResolver.Exclusions);
    public bool CanEditDeleteAdmins => CanEdit(SettingsResolver.DeleteAdmins);
    public bool CanEditDeletableAdmins => CanEdit(SettingsResolver.DeletableAdmins);
    public bool CanEditPolicies => CanEdit(SettingsResolver.Policies);
    public bool CanEditDefaultDuration => CanEdit(SettingsResolver.DefaultPolicyDurationDays);
    public bool CanEditDefaultStrategy => CanEdit(SettingsResolver.DefaultPolicyStrategy);
    public bool CanEditDefaultForce => CanEdit(SettingsResolver.DefaultPolicyForceAtEndOfTerm);
    public bool CanEditEndOfTermDates => CanEdit(SettingsResolver.EndOfTermDates);
    public bool CanEditInventoryPath => CanEdit(SettingsResolver.InventoryPath);

    public string ExclusionsSource => SourceText(SettingsResolver.Exclusions);
    public string DeleteAdminsSource => SourceText(SettingsResolver.DeleteAdmins);
    public string DeletableAdminsSource => SourceText(SettingsResolver.DeletableAdmins);
    public string PoliciesSource => SourceText(SettingsResolver.Policies);
    public string DefaultDurationSource => SourceText(SettingsResolver.DefaultPolicyDurationDays);
    public string DefaultStrategySource => SourceText(SettingsResolver.DefaultPolicyStrategy);
    public string DefaultForceSource => SourceText(SettingsResolver.DefaultPolicyForceAtEndOfTerm);
    public string EndOfTermDatesSource => SourceText(SettingsResolver.EndOfTermDates);
    public string InventoryPathSource => SourceText(SettingsResolver.InventoryPath);

    public bool IsExclusionsLocked => _managedKeys.Contains(SettingsResolver.Exclusions);
    public bool IsDeleteAdminsLocked => _managedKeys.Contains(SettingsResolver.DeleteAdmins);
    public bool IsDeletableAdminsLocked => _managedKeys.Contains(SettingsResolver.DeletableAdmins);
    public bool IsPoliciesLocked => _managedKeys.Contains(SettingsResolver.Policies);
    public bool IsDefaultPolicyLocked =>
        _managedKeys.Contains(SettingsResolver.DefaultPolicyDurationDays) ||
        _managedKeys.Contains(SettingsResolver.DefaultPolicyStrategy) ||
        _managedKeys.Contains(SettingsResolver.DefaultPolicyForceAtEndOfTerm);
    public bool IsEndOfTermDatesLocked => _managedKeys.Contains(SettingsResolver.EndOfTermDates);
    public bool IsInventoryPathLocked => _managedKeys.Contains(SettingsResolver.InventoryPath);

    /// <summary>Settings saved on this device that an elevated user can clear, falling back to Config.yaml or the default.</summary>
    public bool HasMachineSettings => IsElevated && _sources.Any(kv => kv.Value == SettingSource.MachineSettings);

    // ── Elevation State ─────────────────────────────────────────

    /// <summary>True when this process can write HKLM (elevated administrator token).</summary>
    public bool IsElevated { get; } = PrefsElevation.IsProcessElevated();

    public bool IsReadOnly => !IsElevated;

    [ObservableProperty] private string _unlockError = "";

    public bool HasUnlockError => !string.IsNullOrEmpty(UnlockError);

    partial void OnUnlockErrorChanged(string value) => OnPropertyChanged(nameof(HasUnlockError));

    /// <summary>
    /// Relaunches this app elevated through UAC, opening on the Prefs tab. Returns true when the
    /// elevated instance started, so the caller can close this one. A cancelled UAC prompt
    /// returns false quietly and leaves the tab read-only.
    /// </summary>
    public bool TryRelaunchElevated()
    {
        UnlockError = "";
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                UnlockError = "Could not find the app's own executable to relaunch.";
                return false;
            }

            using var process = System.Diagnostics.Process.Start(PrefsElevation.BuildElevatedRelaunch(exe));
            return process is not null;
        }
        catch (Exception ex) when (PrefsElevation.IsElevationCancelled(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            UnlockError = $"Could not relaunch as administrator: {ex.Message}";
            return false;
        }
    }

    // ── Save Status ──────────────────────────────────────────────

    [ObservableProperty] private SaveState _saveStatus = SaveState.Idle;
    [ObservableProperty] private string _saveDetail = "";

    public enum SaveState { Idle, Saving, Saved, Failed }

    partial void OnSaveStatusChanged(SaveState value)
    {
        OnPropertyChanged(nameof(SaveStatusGlyph));
        OnPropertyChanged(nameof(SaveStatusMessage));
        OnPropertyChanged(nameof(IsSaveStatusVisible));
    }

    public string SaveStatusGlyph => SaveStatus switch
    {
        SaveState.Saving => "\uE895",
        SaveState.Saved  => "\uE73E",
        SaveState.Failed => "\uE783",
        _ => ""
    };

    public string SaveStatusMessage => SaveStatus switch
    {
        SaveState.Saving => "Saving...",
        SaveState.Saved  => "Saved to this device",
        SaveState.Failed => $"Save failed: {SaveDetail}",
        _ => ""
    };

    public bool IsSaveStatusVisible => SaveStatus != SaveState.Idle;

    // ── Version Info ─────────────────────────────────────────────

    public string VersionDisplay => $"Version {AppVersion}";

    public static string AppVersion =>
        typeof(PrefsViewModel).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value ?? "dev";

    // ── Load ─────────────────────────────────────────────────────

    public void Load()
    {
        _isLoading = true;

        var policy = RegistrySettingsSource.Policy();
        var file = ConfigFileReader.Read(FileTrust.Default);
        var resolved = SettingsResolver.Resolve(policy, RegistrySettingsSource.MachineSettings(), file.File);

        _sources = resolved.Sources;
        _managedKeys = SettingsResolver.AllSettingNames.Where(n => policy.GetValue(n) != null).ToHashSet();
        ConfigFileWarning = string.Join(Environment.NewLine,
            new[] { file.Warning }.Concat(resolved.Notes).Where(n => !string.IsNullOrEmpty(n)));

        var config = resolved.Config;
        Replace(Exclusions, config.Exclusions);
        Replace(DeletableAdmins, config.DeletableAdmins);
        Replace(EndOfTermDates, config.EndOfTermDates.Select(d => $"{d.Month}-{d.Day}"));
        Rules.Clear();
        foreach (var rule in config.Policies)
            Rules.Add(RuleItem.From(rule, this));
        DeleteAdmins = config.DeleteAdmins;
        DefaultDurationDays = config.DefaultPolicy.DurationDays;
        DefaultStrategy = Strategies.Contains(config.DefaultPolicy.Strategy) ? config.DefaultPolicy.Strategy : Strategies[0];
        DefaultForceAtEndOfTerm = config.DefaultPolicy.ForceAtEndOfTerm;
        InventoryPath = resolved.InventoryPath;
        _dirty.Clear();

        // Notify all bindings (management locks, sources, computed display properties)
        OnPropertyChanged(string.Empty);

        _isLoading = false;
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var v in values)
            target.Add(v);
    }

    // ── List editing ─────────────────────────────────────────────

    public void AddExclusion() => AddName(Exclusions, NewExclusion, () => NewExclusion = "");
    public void AddDeletableAdmin() => AddName(DeletableAdmins, NewDeletableAdmin, () => NewDeletableAdmin = "");

    private static void AddName(ObservableCollection<string> list, string text, Action clear)
    {
        var name = text.Trim();
        if (name.Length == 0) return;
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            list.Add(name);
        clear();
    }

    public void AddTermDate()
    {
        var text = NewTermDate.Trim();
        if (!TermDateText.TryParse(text, out var month, out var day))
        {
            TermDateError = "Enter a month and day, such as 4-30.";
            return;
        }
        TermDateError = "";
        var entry = $"{month}-{day}";
        if (!EndOfTermDates.Contains(entry))
            EndOfTermDates.Add(entry);
        NewTermDate = "";
    }

    public void AddRule() => Rules.Add(new RuleItem(this) { Name = "New rule", DurationDays = 28 });

    public void MoveRule(RuleItem rule, int delta)
    {
        var from = Rules.IndexOf(rule);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rules.Count) return;
        Rules.Move(from, to);
    }

    public bool HasNoRules => Rules.Count == 0;

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoRules));
        MarkDirty(SettingsResolver.Policies);
    }

    /// <summary>The list a removable entry belongs to, by the name its ListView carries in Tag.</summary>
    public ObservableCollection<string>? ListNamed(string? name) => name switch
    {
        SettingsResolver.Exclusions => Exclusions,
        SettingsResolver.DeletableAdmins => DeletableAdmins,
        SettingsResolver.EndOfTermDates => EndOfTermDates,
        _ => null
    };

    /// <summary>Called by a rule when one of its fields changes.</summary>
    internal void RuleEdited() => MarkDirty(SettingsResolver.Policies);

    // ── Auto-Save ─────────────────────────────────────────────────

    private void MarkDirty(string key)
    {
        // Not elevated: HKLM is not writable and the tab is read-only, so never save.
        if (_isLoading || !IsElevated || _managedKeys.Contains(key))
            return;

        lock (_dirty)
            _dirty.Add(key);

        _autoSaveTimer?.Dispose();
        _autoSaveTimer = new System.Threading.Timer(_ => _dispatcher?.TryEnqueue(SaveDirty), null, 600, System.Threading.Timeout.Infinite);
    }

    private void SaveDirty()
    {
        Dictionary<string, object?> values;
        lock (_dirty)
        {
            values = _dirty.ToDictionary(k => k, ValueOf);
            _dirty.Clear();
        }
        if (values.Count == 0) return;
        Save(values);
    }

    /// <summary>Removes every machine setting, so Config.yaml and the defaults apply again.</summary>
    public void ClearMachineSettings()
    {
        Save(SettingsResolver.AllSettingNames.Where(n => !_managedKeys.Contains(n)).ToDictionary(n => n, _ => (object?)null));
        Load();
    }

    private void Save(Dictionary<string, object?> values)
    {
        SaveStatus = SaveState.Saving;
        try
        {
            MachineSettingsStore.Save(values, RegistrySettingsSource.Policy());
            foreach (var key in values.Keys)
                _sources[key] = values[key] == null ? SettingSource.Default : SettingSource.MachineSettings;
            SaveStatus = SaveState.Saved;
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex)
        {
            SaveDetail = ex.Message;
            SaveStatus = SaveState.Failed;
        }
    }

    private object? ValueOf(string key) => key switch
    {
        SettingsResolver.Exclusions => Exclusions.ToList(),
        SettingsResolver.DeleteAdmins => DeleteAdmins,
        SettingsResolver.DeletableAdmins => DeletableAdmins.ToList(),
        SettingsResolver.Policies => Rules.Select(r => r.ToRule()).ToList(),
        SettingsResolver.DefaultPolicyDurationDays => (int)DefaultDurationDays,
        SettingsResolver.DefaultPolicyStrategy => DefaultStrategy,
        SettingsResolver.DefaultPolicyForceAtEndOfTerm => DefaultForceAtEndOfTerm,
        SettingsResolver.EndOfTermDates => EndOfTermDates
            .Select(t => TermDateText.TryParse(t, out var m, out var d) ? new TermDate { Month = m, Day = d } : null)
            .OfType<TermDate>().ToList(),
        SettingsResolver.InventoryPath => string.IsNullOrWhiteSpace(InventoryPath) ? null : InventoryPath.Trim(),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
}

/// <summary>One deletion rule as the Prefs tab edits it. Empty match fields are left out.</summary>
public partial class RuleItem(PrefsViewModel owner) : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _catalog = "";
    [ObservableProperty] private string _area = "";
    [ObservableProperty] private string _room = "";
    [ObservableProperty] private string _usage = "";
    [ObservableProperty] private double _durationDays;
    [ObservableProperty] private string _strategy = "login_and_creation";
    [ObservableProperty] private bool _forceAtEndOfTerm;

    /// <summary>True while <see cref="From"/> fills the fields, which is loading, not editing.</summary>
    private bool _filling;

    public int StrategyIndex
    {
        get => Math.Max(0, Array.IndexOf(PrefsViewModel.Strategies, Strategy));
        set { if (value >= 0 && value < PrefsViewModel.Strategies.Length) Strategy = PrefsViewModel.Strategies[value]; }
    }

    partial void OnStrategyChanged(string value) => OnPropertyChanged(nameof(StrategyIndex));

    public bool IsEditable => owner.CanEditPolicies;

    /// <summary>A one-line reading of the rule: what it matches and what it does.</summary>
    public string Summary
    {
        get
        {
            var match = new[] { ("catalog", Catalog), ("area", Area), ("room", Room), ("usage", Usage) }
                .Where(m => !string.IsNullOrWhiteSpace(m.Item2))
                .Select(m => $"{m.Item1} {m.Item2}")
                .ToList();
            var when = match.Count == 0 ? "Every device" : string.Join(", ", match);
            var action = DurationDays < 0
                ? "never delete"
                : $"delete after {DurationDays:0} days ({(Strategy == "creation_only" ? "by creation date" : "by last login and creation")})";
            return $"{when}: {action}{(ForceAtEndOfTerm ? ", and everything at end of term" : "")}";
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Summary) or nameof(IsEditable) or nameof(StrategyIndex)) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Summary)));
        if (!_filling) owner.RuleEdited();
    }

    public static RuleItem From(PolicyRule rule, PrefsViewModel owner)
    {
        var item = new RuleItem(owner) { _filling = true };
        item.Name = rule.Name;
        item.Catalog = rule.Match.Catalog ?? "";
        item.Area = rule.Match.Area ?? "";
        item.Room = rule.Match.Room ?? "";
        item.Usage = rule.Match.Usage ?? "";
        item.DurationDays = rule.DurationDays;
        item.Strategy = PrefsViewModel.Strategies.Contains(rule.Strategy) ? rule.Strategy : PrefsViewModel.Strategies[0];
        item.ForceAtEndOfTerm = rule.ForceAtEndOfTerm;
        item._filling = false;
        return item;
    }

    public PolicyRule ToRule() => new()
    {
        Name = Name.Trim(),
        Match = new MatchCriteria
        {
            Catalog = Blank(Catalog),
            Area = Blank(Area),
            Room = Blank(Room),
            Usage = Blank(Usage)
        },
        DurationDays = (int)DurationDays,
        Strategy = Strategy,
        ForceAtEndOfTerm = ForceAtEndOfTerm
    };

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
