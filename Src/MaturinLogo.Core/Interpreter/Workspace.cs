using MaturinLogo.Core.Reader;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Interpreter;

/// <summary>
/// The Logo workspace: global variables and user-defined procedures. Names are stored folded to
/// upper case unless the source used |case-preserving bars|, matching Logo's behaviour.
/// </summary>
public sealed class Workspace
{
    private readonly Dictionary<string, LogoValue> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UserProcedure> _procedures = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LogoValue> Globals => _globals;
    public IReadOnlyDictionary<string, UserProcedure> Procedures => _procedures;

    /// <summary>When false, |case| words and quoted words keep their mixed case.</summary>
    public bool FoldCase { get; set; } = true;

    public bool TryGetGlobal(string name, out LogoValue? value) => _globals.TryGetValue(name, out value);

    public void SetGlobal(string name, LogoValue value) => _globals[name] = value;

    public bool RemoveGlobal(string name) => _globals.Remove(name);

    public bool TryGetProcedure(string name, out UserProcedure? procedure) => _procedures.TryGetValue(name, out procedure);

    public void SetProcedure(UserProcedure procedure) => _procedures[procedure.Name] = procedure;

    public bool RemoveProcedure(string name) => _procedures.Remove(name);

    public bool HasProcedure(string name) => _procedures.ContainsKey(name);

    /// <summary>How many inputs a bare call of this procedure takes; 0 when it is not defined.</summary>
    public int ProcedureRequiredInputs(string name) =>
        _procedures.TryGetValue(name, out UserProcedure? proc) && proc is not null ? proc.RequiredInputs : 0;

    /// <summary>The procedure by this name, or a Logo error naming it.</summary>
    public UserProcedure RequireProcedure(string name) =>
        _procedures.TryGetValue(name, out UserProcedure? proc) && proc is not null
            ? proc
            : throw new LogoException(LogoErrorCode.NotDefined, $"I don't know the name {name}",
                $"Type TO {name} ... END to define it first.", subject: name);

    /// <summary>
    /// Properties stored with PUT, keyed by name then property. Logo's property lists are how a
    /// child keeps several facts about one thing, so they live beside the globals.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, LogoValue>> _properties = new(StringComparer.Ordinal);

    public void PutProperty(string name, string property, LogoValue value)
    {
        if (!_properties.TryGetValue(name, out var bag))
            _properties[name] = bag = new Dictionary<string, LogoValue>(StringComparer.Ordinal);
        bag[property] = value;
    }

    public bool GetProperty(string name, string property, out LogoValue? value)
    {
        value = null;
        return _properties.TryGetValue(name, out var bag) && bag.TryGetValue(property, out value);
    }

    public void RemoveProperty(string name, string property)
    {
        if (_properties.TryGetValue(name, out var bag)) bag.Remove(property);
    }

    public IReadOnlyDictionary<string, Dictionary<string, LogoValue>> Properties => _properties;

    /// <summary>Names of every defined procedure, sorted, for LISTPROC/NAMES.</summary>
    public List<string> ProcedureNames()
    {
        var names = _procedures.Keys.ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public List<string> GlobalNames()
    {
        var names = _globals.Keys.ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>True if a name belongs to a Logo primitive; used to refuse TO SQUARE when SQUARE is built in.</summary>
    public Func<string, bool>? IsPrimitiveName { get; set; }

    public void Clear()
    {
        _globals.Clear();
        _procedures.Clear();
        _properties.Clear();
    }

    /// <summary>
    /// Rebuild the workspace from saved source, so a session can be restored from its .logo text
    /// alone. Procedures and variables are replayed through the engine.
    /// </summary>
    public void RestoreFrom(WorkspaceSnapshot snapshot)
    {
        Clear();
        foreach (var (name, value) in snapshot.Globals) _globals[name] = value;
        foreach (var proc in snapshot.Procedures) _procedures[proc.Name] = proc;
    }
}

/// <summary>A saved workspace, used by session persistence.</summary>
public sealed class WorkspaceSnapshot
{
    public List<(string Name, LogoValue Value)> Globals { get; } = new();
    public List<UserProcedure> Procedures { get; } = new();

    public static WorkspaceSnapshot Capture(Workspace workspace)
    {
        var snapshot = new WorkspaceSnapshot();
        foreach (var kv in workspace.Globals) snapshot.Globals.Add((kv.Key, kv.Value));
        foreach (var kv in workspace.Procedures) snapshot.Procedures.Add(kv.Value);
        return snapshot;
    }
}

/// <summary>
/// Flattens a procedure body back into Logo source text so TEXT and the .logo exporter can show
/// or save what the child wrote.
/// </summary>
public static class ProcedureText
{
    public static string Format(UserProcedure proc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("TO ").Append(proc.Name);
        foreach (var f in proc.Formals)
        {
            sb.Append(' ');
            switch (f.Kind)
            {
                case FormalKind.Required:
                    sb.Append(':').Append(f.Name);
                    break;
                case FormalKind.Optional:
                    sb.Append("[:").Append(f.Name).Append(" ").Append(Printer.Format(f.Default ?? LogoValue.None, true)).Append(']');
                    break;
                case FormalKind.Rest:
                    sb.Append("[:").Append(f.Name).Append(']');
                    break;
            }
        }
        sb.AppendLine();
        foreach (var item in proc.Body)
            sb.Append("  ").Append(item.ToString()).AppendLine();
        sb.Append("END");
        return sb.ToString();
    }

    /// <summary>Turn read items back into one line of Logo source.</summary>
    public static string FormatItems(IEnumerable<ProgramItem> items) => string.Join(" ", items);
}
