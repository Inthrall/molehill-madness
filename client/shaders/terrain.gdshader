shader_type canvas_item;

// Turns the cell grid into a curve.
//
// The terrain is a grid of cells and always will be: the simulation owns it, it is hashed, and
// every rule about digging and blasting is expressed in cells. What it does not have to be is a
// grid on screen. Sampling the grid as a field, blurring it a little and cutting it at a level
// gives a smooth boundary that follows the cells without being made of them, which is both the
// cartoon look this game wants and cheap: no contour extraction, no mesh to rebuild when somebody
// digs, and no per-frame work on the processor at all. The dirty-region upload the client already
// does is the whole cost.
//
// One pass covers the pane, sky included, so there is no separate sky to keep behind the ground and
// no second draw to keep in order with it. Anything off the map reads as sky.
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

uniform vec4 ground_colour : source_color;
uniform vec4 edge_colour : source_color;
uniform vec4 sky_colour : source_color;

// Cells the blur reaches. Larger rounds harder; too large and a thin wall dissolves.
uniform float blur_cells = 1.5;

// How sharply the cut lands. Small is crisp, which is what a cartoon wants.
uniform float softness = 0.09;

// Half-width of the outline, in field units either side of the cut.
uniform float edge_width = 0.17;

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

void fragment()
{
    vec2 uv = ((UV * pane_size) - map_origin) / map_extent;

    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        COLOR = sky_colour;
    }
    else
    {
        float here = solidness(TEXTURE, uv);
        float body = smoothstep(0.5 - softness, 0.5 + softness, here);
        float line = 1.0 - smoothstep(0.0, edge_width, abs(here - 0.5));

        COLOR = mix(mix(sky_colour, ground_colour, body), edge_colour, line);
    }
}
