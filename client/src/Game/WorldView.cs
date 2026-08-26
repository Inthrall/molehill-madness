using System.Collections.Generic;
using Godot;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// One window onto the field, with its own camera and its own zoom.
/// </summary>
/// <remarks>
/// A Control rather than a SubViewport, and clipped to its own rectangle. Sharing one World2D
/// between four SubViewports is the more usual Godot answer, but it means four cameras chasing
/// one canvas and a lot of plumbing to get the seams right. Drawing the world once per view
/// into a clipped Control costs a handful of extra draw calls for a texture and sixteen moles,
/// which is nothing, and it puts the split exactly where the layout says it is.
///
/// A view owns no state about the match. It reads the shared stage and its own camera, so
/// four of them can look at the same moment from four places without any of them disagreeing
/// about what happened.
/// </remarks>
public partial class WorldView : Control
{
    private readonly Stage _stage;
    private Vector2 _cameraAt;
    private bool _cameraPlaced;
    private float _scale = 40f;
    private int _seat = -1;

    public WorldView(Stage stage)
    {
        _stage = stage;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Which platoon this view belongs to, or -1 when everybody is sharing it.</summary>
    public int Seat => _seat;

    /// <summary>Takes up the pane the layout has given it.</summary>
    public void Occupy(SplitLayout.Pane pane, double delta)
    {
        Position = pane.Rect.Position;
        Size = pane.Rect.Size;

        if (_seat != pane.Seat)
        {
            // A view that has changed hands should not glide across the map to its new
            // subject; it should already be looking at it.
            _seat = pane.Seat;
            _cameraPlaced = false;
        }

        _scale = pane.PixelsPerMetre;
        Chase(delta);
        QueueRedraw();
    }

    /// <summary>Turns a point on this pane into a point in the world.</summary>
    public Vec2 ToWorld(Vector2 onPane)
    {
        Vector2 metres = (onPane - Offset()) / _scale;

        return new Vec2(
            Fix64.Ratio((int)(metres.X * 256f), 256),
            Fix64.Ratio((int)(metres.Y * 256f), 256));
    }

    // ---- Camera ---------------------------------------------------------------------

    private void Chase(double delta)
    {
        // Biased so the subject sits below the middle of the pane. A mole centred exactly puts
        // half the view underground, which early in a match is undug soil and nothing else,
        // while the sky above it is where the shells actually go.
        Vector2 target = Clamped(Subject() - new Vector2(0, Size.Y * SkyBias));

        if (!_cameraPlaced)
        {
            _cameraAt = target;
            _cameraPlaced = true;
            return;
        }

        _cameraAt = _cameraAt.Lerp(target, Mathf.Min(1f, (float)delta * CameraChase));
    }

    /// <summary>How eagerly the camera chases, per second.</summary>
    private const float CameraChase = 6f;

    /// <summary>How far below the middle of the pane the subject sits, as a share of height.</summary>
    private const float SkyBias = 0.08f;

    /// <summary>
    /// What this view is looking at. Its own platoon's mole when it has one, and otherwise
    /// the middle of whatever is happening.
    /// </summary>
    private Vector2 Subject()
    {
        if (_seat >= 0)
        {
            return Actor(_seat);
        }

        Vector2 total = Vector2.Zero;
        int counted = 0;

        for (int seat = 0; seat < _stage.Planners.Length; seat++)
        {
            if (_stage.Planners[seat].Actor is null)
            {
                continue;
            }

            total += Actor(seat);
            counted++;
        }

        return counted == 0 ? Vector2.Zero : total / counted;
    }

    private Vector2 Actor(int seat)
    {
        if (seat >= _stage.Planners.Length)
        {
            return Vector2.Zero;
        }

        Mole? actor = _stage.Planners[seat].Actor;

        if (actor is null)
        {
            return Vector2.Zero;
        }

        RoundRecording? recording = _stage.Recording;

        if (_stage.Planning || recording is null)
        {
            return ToPixels(actor.Position);
        }

        int slot = SlotOf(actor);

        if (slot < 0)
        {
            return ToPixels(actor.Position);
        }

        // An actor that has gone out is followed to where it went out, not past it.
        int tick = _stage.ExitTick.Length > slot && _stage.ExitTick[slot] >= 0
            ? Mathf.Min(_stage.Tick, _stage.ExitTick[slot])
            : _stage.Tick;

        return ToPixels(recording.PositionOf(tick, slot));
    }

    /// <summary>
    /// Keeps the void past the edge of the map off the screen, except upwards.
    /// </summary>
    /// <remarks>
    /// The sky is painted rather than being part of the map, so the camera is free to rise
    /// above the map's top edge and the pane simply shows more air. It has to be: the map's
    /// generated surface sits about a quarter of the way down, which is less headroom than a
    /// pane wants, and clamping to the map top ate the whole sky bias and left half of every
    /// pane looking at undug soil.
    /// </remarks>
    private Vector2 Clamped(Vector2 focus)
    {
        float cell = _scale / WorldScale.CellsPerMetre;
        float worldWidth = _stage.MapWidthCells * cell;
        float worldHeight = _stage.MapHeightCells * cell;
        float halfWidth = Size.X / 2f;
        float halfHeight = Size.Y / 2f;
        float ceiling = -halfHeight;

        return new Vector2(
            Mathf.Clamp(focus.X, halfWidth, Mathf.Max(halfWidth, worldWidth - halfWidth)),
            Mathf.Clamp(focus.Y, ceiling, Mathf.Max(ceiling, worldHeight - halfHeight)));
    }

    private Vector2 Offset() => (Size / 2f) - _cameraAt;

    private Vector2 ToPixels(Vec2 metres) =>
        new Vector2((float)metres.X.ToDecimal(), (float)metres.Y.ToDecimal()) * _scale;

    // ---- Drawing --------------------------------------------------------------------

    public override void _Draw()
    {
        // The sky, painted rather than mapped, so a pane is never looking at nothing and the
        // camera is free to rise above the top of the map.
        DrawRect(new Rect2(Vector2.Zero, Size), Palette.Paper);

        // Everything below is in world pixels; the transform puts them on the pane, and
        // ClipContents keeps them inside it.
        DrawSetTransform(Offset(), 0, Vector2.One);

        DrawGround();
        DrawLava();
        DrawCrates();

        if (!_stage.Planning && _stage.Recording is not null)
        {
            DrawReplay(_stage.Recording, _stage.Result!);
        }
        else
        {
            DrawStandingMoles();
            DrawPlans();
        }

        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawGauges();
        DrawFrame();
    }

    /// <summary>
    /// This platoon's own instruments, in one strip along the top of its own pane.
    /// </summary>
    /// <remarks>
    /// Time and puff are the whole planning decision, so they go where the player is already
    /// looking rather than into a shared strip they would have to find their own row in. With
    /// four players planning at once, a global gauge could only ever belong to one of them.
    ///
    /// All of it along the top, and none of it in the corners. In a four-way split the corners
    /// of all four panes meet in the middle of the screen, which is where the shared clock and
    /// the tally live, so anything put in a corner ends up on top of them.
    /// </remarks>
    private void DrawGauges()
    {
        SeatPlanner? planner = Gauged();

        if (planner is null || !_stage.Planning || planner.Actor is null)
        {
            return;
        }

        float pad = Mathf.Max(Size.Y * 0.025f, 5f);
        float barHeight = Mathf.Max(Size.Y * 0.03f, 7f);
        float height = (barHeight * 2f) + (pad * 3f);

        // Sized to the strip rather than to the pane, so nothing pokes out of the panel it is
        // supposed to be sitting on.
        float glyph = height * 0.8f;
        float tokens = glyph * 0.8f * Mathf.Max(planner.ResetsLeft, 1);
        float barWidth = Mathf.Min(Size.X - (glyph * 1.4f) - tokens - (pad * 6f), 240f);
        Color seat = Palette.Seat(planner.Seat);

        // A panel behind them. A bar drawn straight onto the sky is unreadable, and one drawn
        // onto soil is worse.
        float width = (glyph * 1.4f) + barWidth + tokens + (pad * 5f);
        DrawRect(new Rect2(pad, pad, width, height), Palette.Panel);

        // What is on the wheel, in the platoon's own colour ring.
        Vector2 wheelAt = new Vector2(pad + pad + (glyph * 0.7f), pad + (height / 2f));
        DrawArc(wheelAt, glyph * 0.5f, 0, Mathf.Tau, 28, new Color(seat, 0.6f), 2f);
        Glyphs.Weapon(this, planner.Weapon, wheelAt, glyph * 0.78f, Palette.OnPanel);

        float left = pad + (glyph * 1.4f) + (pad * 3f);
        float first = pad + pad;
        float second = first + barHeight + pad;

        Bar(left, first, barWidth, barHeight,
            planner.Preview is null
                ? 0f
                : Mathf.Clamp(planner.Preview.TicksUsed / (float)MatchSettings.TicksPerRound, 0f, 1f),
            new Color(0.306f, 0.510f, 0.651f));
        Glyphs.Time(
            this, new Vector2(left - (pad * 1.6f), first + (barHeight / 2f)),
            barHeight * 1.6f, Palette.OnPanel);

        Bar(left, second, barWidth, barHeight, PuffSpent(planner),
            planner.Preview?.RanOutOfPuff == true ? Palette.Damage : new Color(0.435f, 0.647f, 0.325f));
        Glyphs.Puff(
            this, new Vector2(left - (pad * 1.6f), second + (barHeight / 2f)),
            barHeight * 1.6f, Palette.OnPanel);

        DrawResets(planner, left + barWidth + (pad * 1.5f), pad + (height / 2f), glyph);
    }

    /// <summary>
    /// How many resets are left. The design calls this the most watched glyph on the screen, so
    /// it gets to be obvious, and the ring filling up shows the hold registering.
    /// </summary>
    private void DrawResets(SeatPlanner planner, float x, float y, float glyph)
    {
        for (int token = 0; token < planner.ResetsLeft; token++)
        {
            Vector2 at = new Vector2(x + (glyph * 0.4f) + (token * glyph * 0.8f), y);
            Glyphs.Reset(this, at, glyph * 0.75f, Palette.Damage);
        }

        if (planner.ResetHeld <= 0 || planner.ResetsLeft <= 0)
        {
            return;
        }

        Vector2 first = new Vector2(x + (glyph * 0.4f), y);
        DrawArc(
            first, glyph * 0.5f, -Mathf.Pi / 2f,
            (-Mathf.Pi / 2f) + (Mathf.Tau * (float)Mathf.Min(planner.ResetHeld, 1)),
            24, Palette.OnPanel, 3f);
    }

    private static float PuffSpent(SeatPlanner planner)
    {
        if (planner.Preview is null || planner.Actor is null)
        {
            return 0f;
        }

        float total = (float)planner.Actor.Stamina.ToDecimal();

        return total <= 0
            ? 0f
            : Mathf.Clamp((float)planner.Preview.StaminaSpent.ToDecimal() / total, 0f, 1f);
    }

    /// <summary>Whose instruments this pane shows.</summary>
    private SeatPlanner? Gauged()
    {
        if (_seat >= 0)
        {
            return _seat < _stage.Planners.Length ? _stage.Planners[_seat] : null;
        }

        int sharing = SharedPlanSeat();

        return sharing >= 0 ? _stage.Planners[sharing] : null;
    }

    private void Bar(float x, float y, float width, float height, float fraction, Color fill)
    {
        DrawRect(new Rect2(x, y, width, height), new Color(1, 1, 1, 0.14f));
        DrawRect(new Rect2(x, y, width * fraction, height), fill);
    }

    private void DrawGround()
    {
        float cell = _scale / WorldScale.CellsPerMetre;

        DrawTextureRect(
            _stage.Terrain,
            new Rect2(0, 0, _stage.MapWidthCells * cell, _stage.MapHeightCells * cell),
            false);
    }

    private void DrawLava()
    {
        MoleMatch match = _stage.Match;

        if (match.LavaLine == Fix64.MaxValue)
        {
            return;
        }

        float cell = _scale / WorldScale.CellsPerMetre;
        float width = _stage.MapWidthCells * cell;
        float height = _stage.MapHeightCells * cell;
        float top = ToPixels(new Vec2(Fix64.Zero, match.LavaLine)).Y;

        DrawRect(new Rect2(0, top, width, height - top), Palette.Lava);

        if (match.LavaLeftEdge == Fix64.MinValue)
        {
            return;
        }

        float left = ToPixels(new Vec2(match.LavaLeftEdge, Fix64.Zero)).X;
        float right = ToPixels(new Vec2(match.LavaRightEdge, Fix64.Zero)).X;

        DrawRect(new Rect2(0, 0, left, height), Palette.Lava);
        DrawRect(new Rect2(right, 0, width - right, height), Palette.Lava);
    }

    private void DrawCrates()
    {
        float half = _scale * 0.3f;

        foreach (Crate crate in _stage.Match.Crates)
        {
            if (crate.Gone)
            {
                continue;
            }

            Vector2 at = ToPixels(crate.Position);

            if (!crate.HasLanded)
            {
                // The telegraph. The design wants the scramble for a crate to be something
                // everybody scheduled in advance rather than a surprise.
                for (float y = at.Y - (_scale * 7f); y < at.Y - half; y += _scale * 0.4f)
                {
                    DrawLine(
                        new Vector2(at.X, y), new Vector2(at.X, y + (_scale * 0.2f)),
                        new Color(Palette.Crate, 0.5f), 2f);
                }
            }

            Rect2 box = new Rect2(at.X - half, at.Y - half, half * 2f, half * 2f);
            DrawRect(box, crate.HasLanded ? Palette.Crate : new Color(Palette.Crate, 0.45f));
            DrawRect(box, Palette.Ink, false, 2f);
        }
    }

    private void DrawStandingMoles()
    {
        foreach (Mole mole in _stage.Match.Moles)
        {
            if (mole.IsOffDuty)
            {
                continue;
            }

            DrawMole(ToPixels(mole.Position), mole.Seat, mole.Pluck, IsActing(mole));
        }
    }

    private bool IsActing(Mole mole)
    {
        if (mole.Seat >= _stage.Planners.Length)
        {
            return false;
        }

        SeatPlanner planner = _stage.Planners[mole.Seat];

        // Highlighted in its own pane, and in a shared one, but never in somebody else's.
        return planner.Actor == mole && (_seat < 0 || _seat == mole.Seat);
    }

    private void DrawReplay(RoundRecording recording, RoundResult result)
    {
        for (int slot = 0; slot < recording.MoleCount; slot++)
        {
            if (recording.IsOffDutyAt(_stage.Tick, slot))
            {
                DrawExit(recording, slot);
                continue;
            }

            DrawMole(
                ToPixels(recording.PositionAt(_stage.Seconds, slot)),
                _stage.Match.Moles[slot].Seat,
                recording.PluckOf(_stage.Tick, slot),
                highlight: false);
        }

        float shotRadius = Mathf.Max((float)Projectile.Radius.ToDecimal() * _scale, 3f);

        foreach (Vec2 shot in recording.ShotsAt(_stage.Tick))
        {
            DrawCircle(ToPixels(shot), shotRadius, Palette.Ink);
        }

        DrawDamageNumbers(recording, result);
    }

    /// <summary>
    /// Damage numbers, which rise from where they landed and then get out of the way. Digits
    /// are the one numeral the design keeps, because they read the same in every language.
    /// </summary>
    private void DrawDamageNumbers(RoundRecording recording, RoundResult result)
    {
        int upTo = Mathf.Min(recording.HitsUpTo(_stage.Tick), _stage.HitTick.Length);
        float size = Mathf.Max(_scale * 0.55f, 14f);

        for (int index = 0; index < upTo; index++)
        {
            int age = _stage.Tick - _stage.HitTick[index];

            if (age > Stage.DamageNumberTicks)
            {
                continue;
            }

            BlastHit hit = result.Hits[index];
            int slot = SlotOf(hit.Seat, hit.MoleIndex);

            if (slot < 0)
            {
                continue;
            }

            float life = age / (float)Stage.DamageNumberTicks;
            Vector2 at = ToPixels(recording.PositionOf(_stage.HitTick[index], slot))
                + new Vector2(-size * 0.35f, -(_scale * 0.9f) - (life * _scale * 0.7f));

            DrawString(
                ThemeDB.FallbackFont, at, hit.Damage.ToString(),
                HorizontalAlignment.Left, -1, (int)size,
                new Color(Palette.Damage, 1f - (life * life)));
        }
    }

    /// <summary>
    /// Two of the eight exits, roughed in. The reel is chosen in the simulation, so this only
    /// plays what it was told and anything without an animation yet gets the default.
    /// </summary>
    private void DrawExit(RoundRecording recording, int slot)
    {
        int exitTick = _stage.ExitTick.Length > slot ? _stage.ExitTick[slot] : -1;

        if (exitTick < 0)
        {
            return;
        }

        Mole mole = _stage.Match.Moles[slot];
        KnockoutExit? exit = ExitOf(mole);

        if (exit is null)
        {
            return;
        }

        Vector2 at = ToPixels(recording.PositionOf(exitTick, slot));
        float life = Mathf.Clamp((_stage.Tick - exitTick) / (float)Stage.ExitTicks, 0f, 1f);
        float radius = MoleRadius();
        Color colour = Palette.Seat(mole.Seat);

        if (exit.Value == KnockoutExit.StretcherSquad)
        {
            // Carried off, waving weakly, by two worms and a very small stretcher.
            Vector2 carried = at + new Vector2(life * radius * 3.5f, -life * radius * 0.5f);

            DrawLine(
                carried + new Vector2(-radius, radius * 0.6f),
                carried + new Vector2(radius, radius * 0.6f), Palette.Ink, radius * 0.22f);
            DrawCircle(carried, radius * 0.75f, colour);
            DrawCircle(carried + new Vector2(-radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
            DrawCircle(carried + new Vector2(radius * 1.3f, radius * 0.75f), radius * 0.36f, Palette.Snout);
            return;
        }

        // Spin and poof: spins faster, shrinks to nothing, and goes in a puff of dust that
        // leaves its boots standing. Without the dust it just gets smaller, which reads as a
        // rendering fault rather than as a joke.
        if (life < 1f)
        {
            DrawArc(
                at, radius * (0.6f + (life * 2.2f)), 0, Mathf.Tau, 28,
                new Color(Palette.Dust, 1f - life), radius * 0.3f);
        }

        Vector2 boot = new Vector2(radius * 0.5f, radius * 0.55f);

        DrawRect(new Rect2(at.X - boot.X - 1, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);
        DrawRect(new Rect2(at.X + 1, at.Y + boot.Y, boot.X, boot.Y), Palette.Ink);

        DrawCircle(
            at + new Vector2(Mathf.Cos(life * 24f) * radius * 0.4f, -life * radius * 1.6f),
            Mathf.Lerp(radius, 0f, life),
            colour);
    }

    private float MoleRadius() => (float)MatchSettings.Radius.ToDecimal() * _scale;

    private void DrawMole(Vector2 at, int seat, int pluck, bool highlight)
    {
        float radius = MoleRadius();
        Color colour = Palette.Seat(seat);

        if (highlight)
        {
            DrawCircle(at, radius * 1.6f, new Color(colour, 0.3f));
        }

        DrawCircle(at, radius, colour);
        DrawCircle(at + new Vector2(-radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(radius * 0.45f, -radius * 0.8f), radius * 0.34f, colour);
        DrawCircle(at + new Vector2(0, radius * 0.34f), radius * 0.34f, Palette.Snout);

        // Pluck as a bar over the head. A number there would be unreadable at this size, and
        // wordless is the direction anyway.
        float width = radius * 2.2f;
        float top = at.Y - (radius * 2.1f);
        float thickness = Mathf.Max(radius * 0.28f, 2f);

        DrawRect(new Rect2(at.X - (width / 2f), top, width, thickness), new Color(0, 0, 0, 0.3f));
        DrawRect(
            new Rect2(at.X - (width / 2f), top, width * (pluck / 100f), thickness), colour);
    }

    // ---- The plans ------------------------------------------------------------------

    private void DrawPlans()
    {
        foreach (SeatPlanner planner in _stage.Planners)
        {
            // A player sees only their own ink. Everybody planning at once only works if
            // nobody can read anybody else's plan off the screen, which is the same reason
            // the online version hides which mole is even acting.
            if (planner.Actor is null || (_seat >= 0 && planner.Seat != _seat))
            {
                continue;
            }

            if (_seat < 0 && planner.Seat != SharedPlanSeat())
            {
                continue;
            }

            DrawPlan(planner);
        }
    }

    /// <summary>
    /// In a shared view only one plan can be shown without giving the game away, so it is the
    /// one still being laid. Hotseat on one mouse is exactly this case.
    /// </summary>
    private int SharedPlanSeat()
    {
        foreach (SeatPlanner planner in _stage.Planners)
        {
            if (planner.IsPlanning)
            {
                return planner.Seat;
            }
        }

        return -1;
    }

    private void DrawPlan(SeatPlanner planner)
    {
        Color seat = Palette.Seat(planner.Seat);
        Color ink = new Color(Palette.Ink, 0.6f);
        float radius = MoleRadius();

        if (planner.Preview is not null && planner.Preview.Path.Count > 1)
        {
            IReadOnlyList<Vec2> path = planner.Preview.Path;
            int walked = (int)(planner.GhostClock * MatchSettings.TicksPerSecond) % path.Count;

            for (int index = 1; index < path.Count; index++)
            {
                DrawLine(ToPixels(path[index - 1]), ToPixels(path[index]), ink, radius * 0.2f);
            }

            for (int index = 1; index <= walked; index++)
            {
                DrawLine(ToPixels(path[index - 1]), ToPixels(path[index]), seat, radius * 0.34f);
            }

            // The ghost: the same mole, translucent, walking what was laid, while the real
            // one stands where it is.
            Vector2 ghost = ToPixels(path[walked]);
            DrawCircle(ghost, radius, new Color(seat, 0.4f));
            DrawArc(ghost, radius, 0, Mathf.Tau, 24, seat, radius * 0.16f);
        }

        // The waypoints as laid, so what the pen put down stays distinguishable from what the
        // solver made of it. When those two disagree, that gap is the whole planning game.
        foreach (Vec2 point in planner.Route)
        {
            DrawCircle(ToPixels(point), radius * 0.28f, ink);
        }

        // The charge, where the ghost will drop it. Plant, run, regret, and knowing exactly
        // where you left it is the whole difference between the first two and the third.
        if (planner.Charge is not null)
        {
            Vector2 planted = ToPixels(planner.Muzzle) + new Vector2(0, radius * 0.6f);

            Glyphs.Dynamite(this, planted, radius * 1.6f, Palette.Damage);
            DrawArc(
                planted, (float)WeaponTable.Of(WeaponId.BoomBeets).BlastRadius.ToDecimal() * _scale,
                0, Mathf.Tau, 40, new Color(Palette.Damage, 0.28f), 2f);
        }

        DrawAim(planner);
    }

    /// <summary>
    /// Where the shot goes, from where the mole will be standing when it fires rather than
    /// from where it is standing now. Without the outline marking that spot the arrow looks
    /// like it belongs to nobody.
    /// </summary>
    private void DrawAim(SeatPlanner planner)
    {
        Vector2 muzzle = ToPixels(planner.Muzzle);
        float radius = MoleRadius();

        if (planner.Aiming)
        {
            DrawArc(muzzle, radius, 0, Mathf.Tau, 20, Palette.Aiming, radius * 0.16f);
            DrawLine(muzzle, ToPixels(planner.AimAt), Palette.Aiming, radius * 0.2f);
            DrawCircle(ToPixels(planner.AimAt), radius * 0.45f, Palette.Aiming);
            return;
        }

        if (planner.Shot is null)
        {
            return;
        }

        Vec2 aim = planner.Shot.Value.AimDirection();
        Vector2 direction = new Vector2((float)aim.X.ToDecimal(), (float)aim.Y.ToDecimal());
        float length = _scale * 2.6f * (planner.Shot.Value.Power / 255f);
        Vector2 tip = muzzle + (direction * length);
        Vector2 across = new Vector2(-direction.Y, direction.X) * radius * 0.45f;

        DrawArc(muzzle, radius, 0, Mathf.Tau, 20, Palette.Damage, radius * 0.16f);
        DrawLine(muzzle, tip, Palette.Damage, radius * 0.2f);
        DrawColoredPolygon(
            new[] { tip + (direction * radius * 0.8f), tip + across, tip - across },
            Palette.Damage);
    }

    /// <summary>
    /// A border in the platoon's colour, so four views never get mistaken for each other.
    /// </summary>
    private void DrawFrame()
    {
        if (_seat < 0)
        {
            return;
        }

        Rect2 edge = new Rect2(Vector2.Zero, Size);
        DrawRect(edge, Palette.Seat(_seat), false, SplitLayout.Gutter);

        // Dimmed when this platoon has already committed, so at a glance the table can see
        // who everybody is still waiting for.
        if (_seat < _stage.Planners.Length
            && _stage.Planning
            && _stage.Planners[_seat].Committed)
        {
            DrawRect(edge, new Color(Palette.Ink, 0.35f));
        }
    }

    // ---- Lookups --------------------------------------------------------------------

    private int SlotOf(Mole mole) => SlotOf(mole.Seat, mole.Index);

    private int SlotOf(int seat, int moleIndex)
    {
        IReadOnlyList<Mole> moles = _stage.Match.Moles;

        for (int slot = 0; slot < moles.Count; slot++)
        {
            if (moles[slot].Seat == seat && moles[slot].Index == moleIndex)
            {
                return slot;
            }
        }

        return -1;
    }

    private KnockoutExit? ExitOf(Mole mole)
    {
        if (_stage.Result is null)
        {
            return null;
        }

        foreach (Knockout knockout in _stage.Result.Knockouts)
        {
            if (knockout.Seat == mole.Seat && knockout.MoleIndex == mole.Index)
            {
                return knockout.Exit;
            }
        }

        return null;
    }
}
