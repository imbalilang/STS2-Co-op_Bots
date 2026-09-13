using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBots;

// Local-only route overlay. Draws the recommended full-act path in the game's
// line style, but as our own node: it never touches the synced multiplayer
// drawing tool, so it cannot mix with or clear a human's own strokes. Every
// client computes the same plan locally.
internal static class MapRouteOverlay
{
    private static readonly FieldInfo? MapPoints = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary");
    private static Line2D? line;
    private static string signature = "";
    private static bool warned;

    internal static void Update(RunState state)
    {
        try
        {
            var screen = NMapScreen.Instance;
            if (screen is null || !screen.IsOpen) { Clear(); return; }
            var human = state.Players.FirstOrDefault(p => !BotRegistry.IsBot(p.NetId));
            if (human is null) { Clear(); return; }
            var plan = RoutePlanner.Plan(state, human);
            if (plan is null || plan.Path.Count < 2) { Clear(); return; }
            var points = Resolve(screen, plan.Path);
            if (points.Count < 2) { Clear(); return; }
            var next = string.Join(",", plan.Path.Select(p => p.coord.ToString()));
            if (next == signature && line is not null && GodotObject.IsInstanceValid(line))
            {
                Reposition(screen, points);
                return;
            }
            Clear();
            signature = next;
            line = new Line2D
            {
                Width = 7f,
                DefaultColor = new Color(1f, 0.85f, 0.2f, 0.9f),
                ZIndex = 200,
                Antialiased = true,
            };
            screen.Drawings.AddChild(line);
            Reposition(screen, points);
        }
        catch (Exception error)
        {
            Clear();
            if (warned) return;
            warned = true;
            Log.Warn($"CoopBots map route overlay failed; hiding it: {error.GetBaseException()}");
        }
    }

    private static List<Vector2> Resolve(NMapScreen screen, IReadOnlyList<MapPoint> path)
    {
        var result = new List<Vector2>();
        if (MapPoints?.GetValue(screen) is not Dictionary<MapCoord, NMapPoint> map) return result;
        foreach (var point in path)
            if (map.TryGetValue(point.coord, out var node) && GodotObject.IsInstanceValid(node))
                result.Add(node.GlobalPosition);
        return result;
    }

    private static void Reposition(NMapScreen screen, List<Vector2> globalPoints)
    {
        if (line is null || !GodotObject.IsInstanceValid(line)) return;
        var origin = screen.Drawings.GlobalPosition;
        line.ClearPoints();
        foreach (var point in globalPoints) line.AddPoint(point - origin);
    }

    private static void Clear()
    {
        signature = "";
        if (line is not null && GodotObject.IsInstanceValid(line)) line.QueueFree();
        line = null;
    }
}
