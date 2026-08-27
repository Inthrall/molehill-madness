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

// Solidness hereabouts: nine taps, which is what turns a staircase into a curve.
float solidness(sampler2D field, vec2 uv)
{
    vec2 reach = field_texel * blur_cells;
    float total = 0.0;

    for (int down = -1; down <= 1; down++)
    {
        for (int across = -1; across <= 1; across++)
        {
            total += texture(field, uv + (vec2(float(across), float(down)) * reach)).r;
        }
    }

    return total / 9.0;
}

// Which side of the split this column is, as a fraction of the map's height.
float split_row(float across)
{
    // Clamped, so a column off either end of the map keeps the split the end column had rather
    // than wrapping round to the other side of the world.
    vec2 packed = texture(split, vec2(clamp(across, 0.0, 1.0), 0.5)).rg;

    return ((packed.r * 255.0 * 256.0) + (packed.g * 255.0)) / 65535.0;
}

// Whatever is behind the ground here: countryside above the split, soil below it.
vec4 backdrop(vec2 uv)
{
    if (uv.y < split_row(uv.x))
    {
        vec2 at = (uv * sky_repeat) - sky_shift;

        return texture(sky, vec2(at.x, clamp(at.y, sky_texel, 1.0 - sky_texel)));
    }

    return texture(deep, (uv * deep_repeat) - deep_shift) * vec4(vec3(deep_shade), 1.0);
}

void fragment()
{
    vec2 uv = ((UV * pane_size) - map_origin) / map_extent;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        // No field to sample out here: the terrain texture clamps, so the edge column would smear
        // outwards and read as ground going on for ever.
        COLOR = backdrop(uv);
    }
    else
    {
        float here = solidness(TEXTURE, uv);
        float body = smoothstep(0.5 - softness, 0.5 + softness, here);
        float line = 1.0 - smoothstep(0.0, edge_width, abs(here - 0.5));

        vec4 ground = texture(dirt, (uv * dirt_repeat) - dirt_shift);

        COLOR = mix(mix(backdrop(uv), ground, body), edge_colour, line);
    }
}
