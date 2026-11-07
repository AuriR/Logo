using MaturinLogo.Core.Interpreter;
using MaturinLogo.Core.Values;

namespace MaturinLogo.Core.Primitives;

/// <summary>
/// Variables and the workspace. Logo keeps one global bag of names plus the private locals a
/// procedure declares, which is why MAKE and THING are the first two commands a child learns.
/// </summary>
public static class DataPrimitives
{
    public static void Register(PrimitiveTable t)
    {
        t.Add("MAKE", 2, "Give a variable a value.", "MAKE \"SIZE 50",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = Args.Name(a, 0, "MAKE", e.Mode);
                if (PrimitiveTable.IsReserved(name))
                    throw new LogoException(LogoErrorCode.ReadOnly,
                        $"I can't keep a value in {name} - that is a Logo word",
                        "Pick a name that is not part of Logo itself, like SIZE or TOTAL.", subject: name);
                if (e.Workspace.HasProcedure(name))
                    throw new LogoException(LogoErrorCode.ReadOnly,
                        $"{name} is the name of a procedure, so it can't also hold a value",
                        $"Type ERASE \"{name} to forget the procedure first, or choose another name.", subject: name);
                e.MakeVariable(name, a[1], env);
                return Args.Done();
            }, aliases: new[] { "SET" });

        t.Add("THING", 1, "Report what a variable holds.", "PRINT THING \"SIZE",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = Args.Name(a, 0, "THING", e.Mode);
                if (e.TryLookupVariable(name, env, out LogoValue? value)) return Args.Result(value!);
                throw new LogoException(LogoErrorCode.NoValue, ErrorText.NoValue(name, e.Mode),
                    ErrorText.HintNoValue(name, e.Primitives, e.Workspace), subject: name);
            }, isReporter: true);

        t.Add("NAME", 1, "Report where a value is kept, or nothing when the input is not a name.", "PRINT NAME \"SIZE",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = a[0].AsWordText();
                return Args.Result(e.TryLookupVariable(name, env, out _) ? LogoValue.FromWord(name) : LogoValue.None);
            }, isReporter: true);

        t.Add("ERASE", 1, "Forget one variable or procedure.", "ERASE \"SIZE",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = Args.Name(a, 0, "ERASE", e.Mode);
                bool removed = e.Workspace.RemoveProcedure(name) || e.Workspace.RemoveGlobal(name);
                if (!removed)
                    throw new LogoException(LogoErrorCode.NotDefined,
                        $"I can't erase {name} because it isn't there",
                        "Type NAMES to see what is defined.", subject: name);
                return Args.Done();
            }, aliases: new[] { "ER" });

        t.Add("ERALL", 0, "Forget every procedure and variable written this session.", "ERALL",
            CommandFamily.Data, (e, a, env) => { e.ResetWorkspace(); return Args.Done(); });

        t.Add("NAMES", 0, "Report everything defined, as [variables] [procedures].", "PRINT NAMES",
            CommandFamily.Data, (e, a, env) =>
            {
                var globals = LogoValue.FromList(e.Workspace.GlobalNames().Select(LogoValue.FromWord).ToList());
                var procs = LogoValue.FromList(e.Workspace.ProcedureNames().Select(LogoValue.FromWord).ToList());
                return Args.Result(LogoValue.FromList(new[] { globals, procs }));
            }, isReporter: true, aliases: new[] { "LISTVARS" });

        t.Add("LISTPROC", 0, "Report the names of every procedure the child wrote.", "PRINT LISTPROC",
            CommandFamily.Data, (e, a, env) =>
                Args.Result(LogoValue.FromList(e.Workspace.ProcedureNames().Select(LogoValue.FromWord).ToList())),
            isReporter: true);

        t.Add("PUT", 3, "Store a value under a property of a name, like a tiny notebook.", "PUT \"PLAYER \"SCORE 10",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = Args.Name(a, 0, "PUT", e.Mode);
                string property = Args.Name(a, 1, "PUT", e.Mode);
                e.PropertyPut(name, property, a[2]);
                return Args.Done();
            });

        t.Add("GET", 2, "Read back a property stored with PUT.", "PRINT GET \"PLAYER \"SCORE",
            CommandFamily.Data, (e, a, env) =>
            {
                string name = Args.Name(a, 0, "GET", e.Mode);
                string property = Args.Name(a, 1, "GET", e.Mode);
                if (e.PropertyGet(name, property, out LogoValue? value)) return Args.Result(value!);
                throw new LogoException(LogoErrorCode.NoValue, $"I never stored {property} for {name}",
                    $"Store it first: PUT \"{name} \"{property} 10", subject: name);
            }, isReporter: true);

        t.Add("REMPROP", 2, "Forget one property stored with PUT.", "REMPROP \"PLAYER \"SCORE",
            CommandFamily.Data, (e, a, env) =>
            {
                e.PropertyRemove(Args.Name(a, 0, "REMPROP", e.Mode), Args.Name(a, 1, "REMPROP", e.Mode));
                return Args.Done();
            });
    }
}
