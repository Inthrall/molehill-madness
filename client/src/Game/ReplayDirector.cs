using System.Collections.Generic;
using Godot;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// Points the cameras at a round that has already happened.
/// </summary>
/// <remarks>
/// The design's rule for the resolution beat is that the camera "merges by proximity and splits
/// when the fight spreads", and that it merges "when the acting moles are close enough to share a
/// readable frame". The first implementation only got half of that: it measured whether everything
/// fitted one screen, and if not it fell back to a pane per platoon at a fixed zoom. That is the
/// wrong split. Three moles in a scrum and one off on its own gave four panes, three of them
/// showing the same hillside from the same distance, and none of them framed on anything.
///
/// So the grouping is by proximity rather than by owner. A round has already resolved before its
/// first frame is drawn, which means the whole thing is knowable up front: who was in it, where
/// they went, and how far apart the separate pieces of it were. That is enough to cut the round
/// like a director would, once, before it plays. Deciding it up front is also the only way to stop
/// the screen splitting and merging every time somebody is punted sideways.
///
/// Two numbers come out of it per camera: which moles it is watching, and how far back to stand.
/// The camera then tracks its own subjects while the zoom stays put, so it moves like a camera
/// rather than snapping about, and never re-frames mid-round.
/// </remarks>
public sealed class ReplayDirector
{
    /// <summary>
    /// Most cameras the screen will carry. Four, because that is what the layout table knows how
    /// to carve, and because a fifth pane on a television is a contact sheet rather than a match.
    /// </summary>
    private const int MostCameras = 4;

    /// <summary>Ticks between samples while ranking which pieces to join. See <see cref="Join"/>.</summary>
    private const int JoinStep = 4;

    private readonly int[] _slots;
    private readonly float[] _x;
    private readonly float[] _y;
    private readonly int _ticks;

    private ReplayDirector(int[] slots, float[] x, float[] y, int ticks)
    {
        _slots = slots;
        _x = x;
        _y = y;
        _ticks = ticks;
    }

    /// <summary>
    /// Cuts the round: how many cameras, what each is watching, and how far back each stands.
    /// </summary>
    /// <param name="recording">The round, already resolved.</param>
    /// <param name="exitTick">When each mole left, or -1 if it did not.</param>
    /// <param name="subjects">Mole slots worth pointing a camera at.</param>
    /// <param name="band">The screen to carve up.</param>
    /// <param name="forceSplit">Whether to refuse the single-camera cut, for inspection.</param>
    public static SplitLayout.Pane[] Compose(
        RoundRecording recording,
        int[] exitTick,
        List<int> subjects,
        Rect2 band,
        bool forceSplit)
    {
        if (subjects.Count == 0)
        {
            // A round nobody was in. Should not happen, and a wide shot of the map is a better
            // answer than a crash if it does.
            return SplitLayout.Shared(band);
        }

        ReplayDirector director = Sample(recording, exitTick, subjects);
        Dictionary<int, int[][]> cuts = director.Cuts();

        // Fewest cameras that leaves no piece too spread out to be one shot. Counting up rather
        // than down is what makes the design's preference for one screen the default rather than
        // a special case.
        int fewest = forceSplit ? 2 : 1;
        int bestCameras = fewest;
        int bestSprawl = int.MaxValue;

        for (int cameras = fewest; cameras <= MostCameras; cameras++)
        {
            if (!cuts.TryGetValue(cameras, out int[][]? cut))
            {
                // Fewer subjects than cameras, so this many pieces do not exist.
                continue;
            }

            int sprawl = director.Sprawl(cut, SplitLayout.Grid(cameras, band));

            if (sprawl == 0)
            {
                return director.Frame(cut, SplitLayout.Grid(cameras, band));
            }

            if (sprawl < bestSprawl)
            {
                bestSprawl = sprawl;
                bestCameras = cameras;
            }
        }

        // No cut avoids a sprawling piece, which happens when one mole is launched half the height
        // of the map and no amount of splitting brings it back. Take whichever cut sprawls least,
        // preferring fewer cameras on a tie: extra panes that do not fix the framing are extra
        // panes showing the same thing.
        return cuts.TryGetValue(bestCameras, out int[][]? fallback)
            ? director.Frame(fallback, SplitLayout.Grid(bestCameras, band))
            : SplitLayout.Shared(band);
    }

    /// <summary>
    /// Reads every subject's whole round into plain floats.
    /// </summary>
    /// <remarks>
    /// Floating point is safe here in a way it never is inside the simulation. Nothing this
    /// computes goes anywhere near a plan or a hash: it decides where to stand and how far back,
    /// which is presentation, and two devices watching the same replay through slightly different
    /// framing have still watched the same round.
    ///
    /// A mole that has gone out is held at the spot it went out, so a camera keeps its subject
    /// in shot through the pratfall rather than following a corpse that is no longer being
    /// recorded.
    /// </remarks>
    private static ReplayDirector Sample(
        RoundRecording recording, int[] exitTick, List<int> subjects)
    {
        int ticks = recording.Ticks;
        int count = subjects.Count;
        int[] slots = subjects.ToArray();
        float[] x = new float[count * ticks];
        float[] y = new float[count * ticks];

        for (int subject = 0; subject < count; subject++)
        {
            int slot = slots[subject];
            int last = exitTick.Length > slot && exitTick[slot] >= 0 ? exitTick[slot] : ticks - 1;

            for (int tick = 0; tick < ticks; tick++)
            {
                Vec2 at = recording.PositionOf(tick < last ? tick : last, slot);

                x[(subject * ticks) + tick] = (float)at.X.ToDecimal();
                y[(subject * ticks) + tick] = (float)at.Y.ToDecimal();
            }
        }

        return new ReplayDirector(slots, x, y, ticks);
    }

    /// <summary>
    /// Every way of cutting the round into one, two, three or four pieces.
    /// </summary>
    /// <remarks>
    /// Built by starting with a piece per mole and repeatedly joining the two that make the
    /// tightest pair, which gives nested cuts: the four-camera cut is the three-camera cut with
    /// one of its pieces split, and so on up to everybody in one shot. Nesting is what makes the
    /// choice of camera count a simple walk from one upward rather than four unrelated answers.
    ///
    /// Tightest means the smallest spread once joined, not the shortest distance between them.
    /// Two moles that are close now but run in opposite directions do not belong in one frame, and
    /// distance at any single moment cannot tell you that.
    /// </remarks>
    private Dictionary<int, int[][]> Cuts()
    {
        List<List<int>> pieces = new List<List<int>>(_slots.Length);

        for (int subject = 0; subject < _slots.Length; subject++)
        {
            pieces.Add(new List<int> { subject });
        }

        Dictionary<int, int[][]> cuts = new Dictionary<int, int[][]>();

        while (true)
        {
            if (pieces.Count <= MostCameras && !cuts.ContainsKey(pieces.Count))
            {
                cuts[pieces.Count] = Snapshot(pieces);
            }

            if (pieces.Count == 1)
            {
                return cuts;
            }

            Join(pieces);
        }
    }

    /// <summary>Joins the two pieces that make the tightest one.</summary>
    private void Join(List<List<int>> pieces)
    {
        int bestFirst = 0;
        int bestSecond = 1;
        float best = float.MaxValue;
        List<int> scratch = new List<int>();

        for (int first = 0; first < pieces.Count - 1; first++)
        {
            for (int second = first + 1; second < pieces.Count; second++)
            {
                scratch.Clear();
                scratch.AddRange(pieces[first]);
                scratch.AddRange(pieces[second]);

                // Every fourth tick is plenty to rank candidate pairs against each other, and a
                // sixteen-mole round has a couple of thousand pairs to rank. The framing itself
                // reads every tick, so nothing is approximated that anybody can see.
                Spread(scratch, JoinStep, out float width, out float height);
                float cost = (width * width) + (height * height);

                if (cost >= best)
                {
                    continue;
                }

                best = cost;
                bestFirst = first;
                bestSecond = second;
            }
        }

        pieces[bestFirst].AddRange(pieces[bestSecond]);
        pieces.RemoveAt(bestSecond);
    }

    private static int[][] Snapshot(List<List<int>> pieces)
    {
        int[][] cut = new int[pieces.Count][];

        for (int piece = 0; piece < pieces.Count; piece++)
        {
            cut[piece] = pieces[piece].ToArray();
        }

        return cut;
    }

    /// <summary>How many of a cut's pieces are too spread out to be one shot.</summary>
    private int Sprawl(int[][] cut, Rect2[] cells)
    {
        int sprawling = 0;

        for (int camera = 0; camera < cut.Length && camera < cells.Length; camera++)
        {
            Spread(cut[camera], step: 1, out float width, out float height);
            SplitLayout.ZoomToFit(cells[camera], width, height, out bool compact);

            if (!compact)
            {
                sprawling++;
            }
        }

        return sprawling;
    }

    /// <summary>Works out where each camera stands.</summary>
    private SplitLayout.Pane[] Frame(int[][] cut, Rect2[] cells)
    {
        SplitLayout.Pane[] panes = new SplitLayout.Pane[Mathf.Min(cut.Length, cells.Length)];

        for (int camera = 0; camera < panes.Length; camera++)
        {
            Spread(cut[camera], step: 1, out float width, out float height);

            float zoom = SplitLayout.ZoomToFit(cells[camera], width, height, out bool _);

            panes[camera] = new SplitLayout.Pane(
                cells[camera], seat: -1, zoom, Slots(cut[camera]));
        }

        return panes;
    }

    private int[] Slots(IReadOnlyList<int> piece)
    {
        int[] slots = new int[piece.Count];

        for (int index = 0; index < piece.Count; index++)
        {
            slots[index] = _slots[piece[index]];
        }

        return slots;
    }

    /// <summary>
    /// The widest and tallest a group of moles ever gets at any single moment of the round.
    /// </summary>
    /// <remarks>
    /// At a single moment, deliberately, rather than over the whole round. A camera that tracks
    /// its subjects only ever has to hold what is happening now, so framing for everywhere they
    /// went between them would stand needlessly far back: two moles who walk thirty metres side
    /// by side are a tight shot the entire way, and the box containing both their journeys is
    /// thirty metres wide.
    /// </remarks>
    private void Spread(IReadOnlyList<int> piece, int step, out float width, out float height)
    {
        width = 0f;
        height = 0f;

        for (int tick = 0; tick < _ticks; tick += step)
        {
            float leftmost = float.MaxValue;
            float rightmost = float.MinValue;
            float highest = float.MaxValue;
            float lowest = float.MinValue;

            for (int index = 0; index < piece.Count; index++)
            {
                int at = (piece[index] * _ticks) + tick;

                leftmost = Mathf.Min(leftmost, _x[at]);
                rightmost = Mathf.Max(rightmost, _x[at]);
                highest = Mathf.Min(highest, _y[at]);
                lowest = Mathf.Max(lowest, _y[at]);
            }

            width = Mathf.Max(width, rightmost - leftmost);
            height = Mathf.Max(height, lowest - highest);
        }
    }
}
