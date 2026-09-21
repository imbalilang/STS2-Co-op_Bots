using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;

namespace CoopBots.Kernel;

/// <summary>
/// CombatSolver 0.41.0, reached through the assembly that ships beside this kernel.
///
/// WHY A SECOND ENGINE. 0.41 replaced <c>SimulatedCombatState</c> with
/// <c>CombatPredictionState</c> and changed the simulator's constructor with it. Every
/// type in this kernel — <c>KernelSession</c>, <c>KernelTeamSearch</c> — is written
/// against the old shape. Shipping 0.41 as its own assembly costs nothing instead: its
/// root namespace is <c>CombatSolver</c>, so its type identities never collide with the
/// vendored engine's <c>CoopBots.Kernel.Vendor</c>, and only game types
/// (<see cref="CombatState"/> and friends, single identity out of sts2.dll) cross.
///
/// The kernel has since re-vendored 0.41.0 wholesale (see VENDOR_DEVIATIONS.md D1), so
/// both assemblies now carry the same version. This probe is kept anyway because it
/// reaches the engine through the assembly's OWN root namespace rather than through our
/// adapter layer — which is what keeps "0.41 runs end to end" separable from "our port of
/// it compiles".
///
/// EVERYTHING IS BY NAME ON PURPOSE. The engine's types are internal, and the policy
/// record's shape has already drifted once (0.41 merged the short and deep profiles into
/// one). Reflecting over the constructor's parameters and filling each by type survives
/// that kind of change; a hard-coded argument list would not. Anything unexpected
/// degrades to "cannot reach the engine", which the caller already handles.
/// </summary>
public static class CombatSolver41
{
    private const string EngineAssembly = "CombatSolver";

    private static bool _failed;
    private static Assembly? _assembly;

    /// <summary>The engine assembly, located among what is already loaded and otherwise
    /// loaded from beside this kernel.</summary>
    private static Assembly? Engine()
    {
        if (_assembly is not null || _failed) return _assembly;
        try
        {
            _assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == EngineAssembly);
            if (_assembly is null)
            {
                var beside = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(CombatSolver41).Assembly.Location) ?? ".",
                    EngineAssembly + ".dll");
                if (System.IO.File.Exists(beside)) _assembly = Assembly.LoadFrom(beside);
            }
            if (_assembly is null) _failed = true;
        }
        catch (Exception)
        {
            _failed = true;
        }
        return _assembly;
    }

    private static Type? Find(string name) => Engine()?.GetType(EngineAssembly + "." + name, throwOnError: false);

    /// <summary>Type lookup into the 0.41 assembly, for the scorer that shares this
    /// engine. Same by-name discipline as everything else here: the engine's types are
    /// internal, and their names are the only stable handle we have across versions.</summary>
    internal static Type? Type(string name) => Find(name);

    /// <summary>True once the engine has proved unreachable, so callers can stop trying.</summary>
    internal static bool Unreachable => _failed;

    /// <summary>Why the last attempt gave up, in enough detail to act on.</summary>
    public static string LastFailure { get; private set; } = "not-attempted";

    /// <summary>
    /// A policy snapshot built by walking the record's own constructor rather than by
    /// naming its arguments. Reference parameters get null and value parameters their
    /// default; the profile slots are filled from whatever static profile members the
    /// type exposes, because those are what the engine's own presets are.
    ///
    /// The nulls are safe for the evaluation path and only for it: the four
    /// session-scoped members (diagnostics, frame/memory pressure, potion strategy) have
    /// zero references in the evaluation file — checked, not assumed, in 0.41's
    /// <c>CombatBeamSolver.StateEvaluation.cs</c>. Anything that builds a solver FROM this
    /// snapshot may dereference them — do not.
    /// </summary>
    private static object? Policy(Type policyType)
    {
        try
        {
            var constructor = policyType.GetConstructors()
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (constructor is null) return null;

            var staticProfiles = new Queue<object?>(policyType.Assembly.GetTypes()
                .Where(type => type.Name == "SolverSearchProfile")
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Static))
                .Where(property => property.PropertyType.Name == "SolverSearchProfile")
                .Select(property => property.GetValue(null))
                .OfType<object>());

            var arguments = constructor.GetParameters().Select(parameter =>
            {
                var type = parameter.ParameterType;
                if (type.Name == "SolverSearchProfile" && staticProfiles.Count > 0) return staticProfiles.Dequeue();
                if (type.Name == "SolverSearchProfile") return null;
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }).ToArray();

            return constructor.Invoke(arguments);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The engine's plan for one seat, or null when anything at all goes wrong.
    /// Soft-fail throughout: a bridge that threw would take the planner with it, and the
    /// planner has no story for "the evaluator vanished".</summary>
    public static object? Solve(CombatState combat)
    {
        try
        {
            if (_failed) return null;
            var coordinator = Find("CombatSearchCoordinator");
            var rootType = Find("CombatRootSnapshot");
            var namesType = Find("SolverDisplayNames");
            var damageType = Find("BattleDamageTracker");
            var policyType = Find("SearchPolicySnapshot");
            if (coordinator is null || rootType is null || namesType is null
                || damageType is null || policyType is null)
            {
                // Name which one. "NOT reachable (assembly, member or policy)" on a live
                // run could not distinguish an unloadable assembly from one missing type
                // from a constructor the reflection could not fill, and those have
                // completely different fixes.
                LastFailure = "missing types:"
                    + (coordinator is null ? " CombatSearchCoordinator" : "")
                    + (rootType is null ? " CombatRootSnapshot" : "")
                    + (namesType is null ? " SolverDisplayNames" : "")
                    + (damageType is null ? " BattleDamageTracker" : "")
                    + (policyType is null ? " SearchPolicySnapshot" : "")
                    + $" (assembly={(Engine()?.FullName ?? "not-found")})";
                _failed = true;
                return null;
            }

            var root = rootType.GetMethod("Capture", new[] { typeof(CombatState) })?.Invoke(null, new object[] { combat });
            var names = namesType.GetMethod("Capture")?.Invoke(null, new object[] { combat });
            var damage = damageType.GetMethod("Observe")?.Invoke(null, new object[] { combat });
            var policy = Policy(policyType);
            if (root is null || names is null || damage is null || policy is null)
            {
                LastFailure = "root-capture=" + (root is null ? "null" : "ok")
                    + " names=" + (names is null ? "null" : "ok")
                    + " damage=" + (damage is null ? "null" : "ok")
                    + " policy=" + (policy is null ? "null" : "ok")
                    + $" (ctypes={policyType.GetConstructors().Length})";
                return null;
            }

            var solve = coordinator.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "Solve" && method.GetParameters().Length >= 4);
            if (solve is null) return null;

            var seats = solve.GetParameters().Select(parameter =>
            {
                var type = parameter.ParameterType;
                if (type == rootType) return root;
                if (type == namesType) return names;
                if (type == damageType) return damage;
                if (type == policyType) return policy;
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }).ToArray();
            return solve.Invoke(null, seats);
        }
        catch (Exception)
        {
            _failed = true;
            return null;
        }
    }
}
