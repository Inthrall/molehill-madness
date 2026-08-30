shader_type canvas_item;

// Turns the cell grid into a curve, and paints what is on either side of it.
//
// The terrain is a grid of cells and always will be: the simulation owns it, it is hashed, and
// every rule about digging and blasting is expressed in cells. What it does not have to be is a
// grid on screen. Sampling the grid as a field, blurring it a little and cutting it at a level
// gives a smooth boundary that follows the cells without being made of them, which is both the
// cartoon look this game wants and cheap: no contour extraction, no mesh to rebuild when somebody
// digs, and no per-frame work on the processor at all. The dirty-region upload the client already
// does is the whole cost.
//
// Three textures fill the two sides of that boundary. Solid ground is the dirt texture; air is
// either the countryside panorama or the deep soil behind a tunnel, and which one it is comes from
// the split, one texel a column. The split is read rather than worked out here on purpose: whether
// a column is above ground or below it is a fact about where its ground started, which is not
// something a fragment can see, because by the time anybody is looking through a tunnel the cells
// that used to be above it are gone. See Backdrop.cs.
//
// All three are placed in world coordinates rather than screen ones, so the ground stays the same
// size in the world however far the camera has zoomed in or panned. That is the difference between
// ground and wallpaper.
//
// One pass covers the pane, sky included, so there is no separate sky to keep behind the ground and
// no second draw to keep in order with it. Anything off the map carries on as backdrop, which is
// what a player sees past the end of the world.
//
// The field is handed to the blur as a parameter rather than read from TEXTURE inside it. Godot only
// exposes the fragment built-ins in fragment() itself, so a helper that reaches for TEXTURE fails to
// compile, and a canvas item whose shader fails to compile silently falls back to drawing the plain
// texture. That is a quiet failure worth knowing about: the render looked wrong rather than broken,
// and the console line saying so was the only evidence.

// Where the map sits on this pane, and how big the pane is, both in pane pixels.
uniform vec2 map_origin;
uniform vec2 map_extent;
uniform vec2 pane_size;

// One cell, in texture coordinates.
uniform vec2 field_texel;

// The destructible ground, and the soil behind a tunnel.
//
// Mipmapped, and that is not a nicety either. A dirt tile is a thousand texels over six metres of
// map, which at the normal framing is about four screen pixels to the texel, so without mipmaps
// every neighbouring fragment reads a texel four apart and the texture cache is useless. Four
// panes of full-screen fragments doing that across three large textures is what a dropped frame
// rate looks like, and the fix costs a third of the texture memory and nothing per frame. The
// imported textures have to agree: mipmaps/generate=true in their .import files.
uniform sampler2D dirt : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D deep : source_color, filter_linear_mipmap, repeat_enable;

// Countryside. Repeats sideways along the world; clamped rather than repeated upwards, because it
// is a strip of horizon and stacking copies of it would put fields in the sky.
uniform sampler2D sky : source_color, filter_linear_mipmap, repeat_enable;

// The row each column's ground started on, packed into two channels. Point-sampled, and it has to
// be: blending one column's high byte into the next produces a row belonging to neither, which
// draws as a spike of soil standing in the sky.
uniform sampler2D split : filter_nearest, repeat_disable;

// How many times each texture fits across the map, and where it starts, in tiles.
uniform vec2 dirt_repeat;
uniform vec2 dirt_shift;
uniform vec2 deep_repeat;
uniform vec2 deep_shift;
uniform vec2 sky_repeat;
uniform vec2 sky_shift;

// Half a texel of the panorama's height, to keep the clamped sample off its own wrap.
uniform float sky_texel;

uniform vec4 edge_colour : source_color;

// Cells the blur reaches. Larger rounds harder; too large and a thin wall dissolves.
uniform float blur_cells = 1.5;

// How many of them to take: nine in a square, or five in a cross for a machine that cannot afford
// nine. Set from Quality.cs rather than guessed at here. The trade is described where solidness is.
uniform int blur_taps = 9;

// How sharply the cut lands. Small is crisp, which is what a cartoon wants.
uniform float softness = 0.09;

// Half-width of the outline, in field units either side of the cut.
uniform float edge_width = 0.17;

// How much darker the soil behind a tunnel is than the soil in front of it.
//
// Not a nicety. The two textures came out of the same generator and sit within a few percent of
// each other in tone, so with this at one a cave read as an outline drawn on solid ground rather
// than as a hole in it, and a tunnel somebody had spent half their stamina digging was invisible
// from two metres away. Darkening the layer behind is the whole reason a player can see depth in a
// flat picture; the outline alone only says where a boundary is, not which side of it is air.
uniform float deep_shade = 0.55;

// Solidness hereabouts: nine taps, which is what turns a staircase into a curve. Five in a cross
// on the low setting, which reaches the same distance with less rounding at the diagonals and is
// four fewer reads on every fragment of the screen.
//
// textureLod at zero rather than texture, for two reasons that both matter. The cell field must
// never be read from a mip: the whole point of it is that a cell is a cell, and a level down from
// that is the boundary blurred away before the blur here has had a look at it. And an implicit
// level is a set of derivatives computed per tap, which is work paid for nine times over for an
// answer that was always going to be zero.
float solidness(sampler2D field, vec2 uv)
{
    vec2 reach = field_texel * blur_cells;

    if (blur_taps < 9)
    {
        return (textureLod(field, uv, 0.0).r
            + textureLod(field, uv + vec2(reach.x, 0.0), 0.0).r
            + textureLod(field, uv - vec2(reach.x, 0.0), 0.0).r
            + textureLod(field, uv + vec2(0.0, reach.y), 0.0).r
            + textureLod(field, uv - vec2(0.0, reach.y), 0.0).r) / 5.0;
    }

    float total = 0.0;

    for (int down = -1; down <= 1; down++)
    {
        for (int across = -1; across <= 1; across++)
        {
            total += textureLod(field, uv + (vec2(float(across), float(down)) * reach), 0.0).r;
        }
    }

    return total / 9.0;
}

// Which side of the split this column is, as a fraction of the map's height.
float split_row(float across)
{
    // Clamped, so a column off either end of the map keeps the split the end column had rather
    // than wrapping round to the other side of the world. Level zero because the split is one row
    // of texels and has no other level to be read from.
    vec2 packed = textureLod(split, vec2(clamp(across, 0.0, 1.0), 0.5), 0.0).rg;

    return ((packed.r * 255.0 * 256.0) + (packed.g * 255.0)) / 65535.0;
}

// Whatever is behind the ground here: countryside above the split, soil below it.
//
// The derivatives are handed in rather than worked out here, and that is a correctness fix rather
// than a saving. A mipmapped texture read inside an if is undefined in GLSL: the level comes from
// how far neighbouring fragments have moved, and neighbours that took the other branch have not
// moved anywhere the hardware can see. Both reads below have always sat inside a branch on the
// split, and the fragment function has since grown two more branches around them, so what level
// any of this was read at was up to the driver. textureGrad says outright, and the derivatives
// come from the one place every fragment passes through.
vec4 backdrop(vec2 uv, vec2 across, vec2 down)
{
    if (uv.y < split_row(uv.x))
    {
        vec2 at = (uv * sky_repeat) - sky_shift;

        return textureGrad(
            sky,
            vec2(at.x, clamp(at.y, sky_texel, 1.0 - sky_texel)),
            across * sky_repeat,
            down * sky_repeat);
    }

    return textureGrad(deep, (uv * deep_repeat) - deep_shift, across * deep_repeat, down * deep_repeat)
        * vec4(vec3(deep_shade), 1.0);
}

void fragment()
{
    vec2 uv = ((UV * pane_size) - map_origin) / map_extent;

    // Once, out here, where every fragment goes: see the note on backdrop. Everything below reads
    // its textures at the level these two describe.
    vec2 across = dFdx(uv);
    vec2 down = dFdy(uv);

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        // No field to sample out here: the terrain texture clamps, so the edge column would smear
        // outwards and read as ground going on for ever.
        COLOR = backdrop(uv, across, down);
    }
    else
    {
        float here = solidness(TEXTURE, uv);
        float body = smoothstep(0.5 - softness, 0.5 + softness, here);
        float line = 1.0 - smoothstep(0.0, edge_width, abs(here - 0.5));

        // Only the side of the boundary this fragment is on. Most of a frame is well inside ground
        // or well inside air, where the mix was being handed one picture at full weight and the
        // other at none and was reading both of them anyway: the sky over a lawn fetched soil, and
        // every fragment of solid ground fetched the countryside behind it and the split that says
        // which countryside it is. The colour is the same either way, since a mix at zero or one is
        // whichever side it was given, and it is two or three fewer texture reads across nearly the
        // whole screen.
        vec4 under;

        if (body >= 1.0)
        {
            under = textureGrad(
                dirt, (uv * dirt_repeat) - dirt_shift, across * dirt_repeat, down * dirt_repeat);
        }
        else if (body <= 0.0)
        {
            under = backdrop(uv, across, down);
        }
        else
        {
            vec4 ground = textureGrad(
                dirt, (uv * dirt_repeat) - dirt_shift, across * dirt_repeat, down * dirt_repeat);

            under = mix(backdrop(uv, across, down), ground, body);
        }

        COLOR = mix(under, edge_colour, line);
    }
}
