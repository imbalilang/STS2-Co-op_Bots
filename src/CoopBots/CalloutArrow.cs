using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CoopBots;

/// <summary>
/// The "play this on that" ask, drawn rather than described: a gold line from
/// each hand card the team wants played to the enemy it finishes, with a pulsing
/// ring on that enemy. Only the peer holding the hand has the card nodes, so a
/// teammate in the same fight sees the ring and the panel text instead.
/// </summary>
internal static class CalloutArrow
{
    private const int CurveSteps = 18, RingSteps = 26;
    private const float LineWidth = 7f, HeadWidth = 6f, RingWidth = 4f;
    private const float RingRadius = 36f, HeadLength = 26f, HeadSpread = 14f;
    private static readonly Color Stroke = BotUiTheme.Accent;
    private static readonly Color Faded = new(BotUiTheme.Accent.R, BotUiTheme.Accent.G, BotUiTheme.Accent.B, 0.55f);

    private static Node2D? root;
    private static readonly List<Line2D> strokes = new();
    private static int used;
    private static long shown;

    /// <summary>
    /// Draws the ask for this frame. <paramref name="hand"/> is the local human
    /// when the request is theirs — anyone else has no card nodes to point at.
    /// </summary>
    internal static void Update(Player? hand, Creature? target, IReadOnlyList<string> cards)
    {
        var node = target is null ? null : NCombatRoom.Instance?.GetCreatureNode(target);
        if (node is null || !GodotObject.IsInstanceValid(node)) { Clear(); return; }
        Ensure();
        if (root is null || !GodotObject.IsInstanceValid(root)) return;
        if (shown == 0) shown = System.Environment.TickCount64;
        used = 0;
        var end = Anchor(node);
        var pulse = RingRadius + Mathf.Sin((System.Environment.TickCount64 - shown) / 260f) * 3f;
        Ring(end, pulse);
        var drawn = 0;
        foreach (var key in cards)
        {
            if (hand is null || drawn >= 2) break;
            var card = FindInHand(hand, key);
            if (card is null) continue;
            // The first card is the one to click now; later ones are its follow-up.
            var start = card.GlobalPosition;
            var direction = (end - start).Normalized();
            var tip = end - direction * pulse;
            Curve(start, tip, drawn == 0 ? Stroke : Faded);
            Head(tip, direction, drawn == 0 ? Stroke : Faded);
            drawn++;
        }
        // The ask was ours and none of its cards is in hand any more: it has been
        // played, so the pointer goes away rather than pointing at nothing.
        if (hand is not null && drawn == 0) { Clear(); return; }
        for (var i = used; i < strokes.Count; i++) strokes[i].Visible = false;
    }

    internal static void Clear()
    {
        used = 0;
        shown = 0;
        strokes.Clear();
        if (root is not null && GodotObject.IsInstanceValid(root)) root.QueueFree();
        root = null;
    }

    private static void Ensure()
    {
        if (root is not null && GodotObject.IsInstanceValid(root)) return;
        Clear();
        var run = NRun.Instance;
        if (run is null) return;
        // Top-level so the points below can be creature/card global positions:
        // the enemies live inside containers the game scales for the aspect ratio.
        // Just under the panel (100): above the fight, never on top of our own card.
        root = new Node2D { Name = "CoopBotsCalloutArrow", TopLevel = true, ZIndex = 99 };
        run.AddChild(root);
    }

    /// <summary>Where an arrow lands: the middle of the creature's hitbox.</summary>
    private static Vector2 Anchor(NCreature creature) => creature.Hitbox.GlobalPosition + creature.Hitbox.Size * 0.5f;

    private static NCard? FindInHand(Player hand, string key)
    {
        var pile = hand.PlayerCombatState?.Hand;
        if (pile is null) return null;
        foreach (var card in pile.Cards)
            if (card.Id.Entry == key)
                return NCard.FindOnTable(card);
        return null;
    }

    private static void Curve(Vector2 from, Vector2 to, Color color)
    {
        var control = ControlPoint(from, to);
        var stroke = StrokeAt(color, LineWidth);
        stroke.ClearPoints();
        for (var i = 0; i <= CurveSteps; i++)
        {
            var t = (float)i / CurveSteps;
            var inverse = 1f - t;
            stroke.AddPoint(inverse * inverse * from + 2f * inverse * t * control + t * t * to);
        }
    }

    /// <summary>
    /// The game's own card arrow sags below the straight line to the target;
    /// matching that control point keeps both arrows reading as one feature.
    /// </summary>
    private static Vector2 ControlPoint(Vector2 from, Vector2 to)
    {
        var sagged = to + new Vector2(0f, 88f);
        return new Vector2(from.X - (sagged.X - from.X) * 0.25f, sagged.Y + (sagged.Y - from.Y) * 0.5f);
    }

    private static void Head(Vector2 tip, Vector2 direction, Color color)
    {
        var stroke = StrokeAt(color, HeadWidth);
        var back = tip - direction * HeadLength;
        var side = direction.Orthogonal() * HeadSpread;
        stroke.ClearPoints();
        stroke.AddPoint(back + side);
        stroke.AddPoint(tip);
        stroke.AddPoint(back - side);
    }

    private static void Ring(Vector2 center, float radius)
    {
        var stroke = StrokeAt(Stroke, RingWidth);
        stroke.ClearPoints();
        for (var i = 0; i <= RingSteps; i++)
        {
            var angle = Mathf.Tau * i / RingSteps;
            stroke.AddPoint(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
    }

    private static Line2D StrokeAt(Color color, float width)
    {
        if (used == strokes.Count)
        {
            var line = new Line2D
            {
                Width = width,
                Antialiased = true,
                JointMode = Line2D.LineJointMode.Round,
                BeginCapMode = Line2D.LineCapMode.Round,
                EndCapMode = Line2D.LineCapMode.Round,
            };
            root!.AddChild(line);
            strokes.Add(line);
        }
        var stroke = strokes[used++];
        stroke.Visible = true;
        stroke.Width = width;
        stroke.DefaultColor = color;
        return stroke;
    }
}
