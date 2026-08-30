using Godot;

/// <summary>
/// The little type the game has: short labels, and nothing longer.
/// </summary>
/// <remarks>
/// This game had no type in it at all, on purpose. The design is wordless, on the grounds that a
/// picture reads the same in every language a seven-year-old might have, and digits are the one
/// exception it keeps because a numeral does too. Everything on screen is a drawn glyph.
///
/// Wordless has a limit, and the menu is where it was reached. Four icons in a row asking where the
/// match is being played is four guesses: a couch, an aerial, two strangers and a grid of tiles are
/// each a reasonable picture of their option and none of them is unambiguous, and unlike a weapon
/// there is no round afterwards to teach you what you picked. So one or two words go under each,
/// which is the smallest amount of type that fixes it.
///
/// Titan One, under the Open Font License, and the same display face the design documents use, so
/// the words look like they belong to the game rather than to the engine. That last part is the whole
/// reason a font is committed rather than falling back on what Godot ships with: the drawn digits
/// exist precisely because a system typeface in a game with no other type in it looked like a bug.
///
/// Kept to labels. If something here ever wants a sentence, that is a sign the picture is not doing
/// its job, and the picture is what should change.
/// </remarks>
public static class Words
{
    private static FontFile? _face;

    /// <summary>The label face, loaded once.</summary>
    public static FontFile Face =>
        _face ??= ResourceLoader.Load<FontFile>("res://font/TitanOne-Regular.ttf");

    /// <summary>
    /// Draws a short label centred on a point.
    /// </summary>
    /// <remarks>
    /// Centred horizontally on the point and sitting below it, because every caller is putting a
    /// name under a picture and would otherwise do the same arithmetic. The baseline offset is part
    /// of that: DrawString places a baseline rather than a top edge, so a caller handing over the
    /// bottom of an icon and expecting the text under it would overlap it by most of a line.
    /// </remarks>
    public static void Under(CanvasItem into, string label, Vector2 at, float size, Color ink)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        int points = Mathf.Max(Mathf.RoundToInt(size), 8);
        Vector2 measured = Face.GetStringSize(
            label, HorizontalAlignment.Left, -1f, points);

        into.DrawString(
            Face,
            new Vector2(at.X - (measured.X / 2f), at.Y + points),
            label,
            HorizontalAlignment.Left,
            -1f,
            points,
            ink);
    }

    /// <summary>How wide a label comes out, for anything that has to lay out around it.</summary>
    public static float Width(string label, float size) =>
        Face.GetStringSize(
            label, HorizontalAlignment.Left, -1f,
            Mathf.Max(Mathf.RoundToInt(size), 8)).X;
}
