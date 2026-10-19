using RPM.Core.Viz;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// A grid, turned inside out, cut to the record.
///
/// <para>Two changes are made to the space before anything is drawn in it. First, it is turned in four
/// dimensions instead of three, in the planes that involve the fourth axis. This is a rotation with no
/// three-dimensional counterpart. It changes what the slice through the space contains, not only where the
/// slice points. Then the space is turned inside out about the origin. Every point is moved to
/// <c>k·p</c>, where <c>k</c> falls off with distance. So what was far away is brought close and packed
/// together, and what was close is pushed outward. This inversion is why an endless flat grid looks like
/// the inside of a cut stone. The cells nearest the middle of the screen are the ones that were furthest
/// away, and they arrive already curved.</para>
///
/// <para>What is drawn is simple: the wires along the cell's four axes, and a bead where they meet. All the
/// shape on screen comes from the two turns.</para>
///
/// <para>The slice moves at a constant rate, one fourth coordinate per beat. So cells grow out of nothing and
/// close again as the slice passes them. Each phrase turns the grid a little further, in a direction that is
/// not on the screen.</para>
/// </summary>
public sealed partial class LatticeStage : MarchedStage
{
    protected override float Bloom => 0.6f + 0.6f * Air;

    protected override float Saturate => 1.35f;

    protected override string MarchSource(string versionDef) => $$"""
        {{versionDef}}
        precision highp float;

        uniform vec2  uResolution;
        uniform vec3  uCamera;    // orbit, fov, sway
        uniform vec4  uSlice;     // w of the slice, and the three turns that involve it
        uniform vec4  uRoom2;     // cell size, wire, bead, how hard the space is turned inside out
        uniform vec3  uBeat;      // beats, pulse, drop
        uniform vec3  uBands;     // bass, mid, air
        uniform float uPalette;
        uniform float uSteps;

        out vec4 fragColor;

        const float TAU = 6.28318530718;

        mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }

        // The grid, after the turning and the inversion. `swell` comes back as how much the inversion
        // magnified this point: near the middle of the screen it is large, which is both how the picture
        // is coloured and what the distance has to be divided by to stay a distance.
        float grid(vec3 p, out float swell, out vec3 cellId, out vec3 offset)
        {
            float size = uRoom2.x, wire = uRoom2.y, bead = uRoom2.z, pull = uRoom2.w;

            vec4 q = vec4(p, uSlice.x);

            // Turned in the three planes that involve the fourth axis. Nothing here is a rotation the
            // camera could have made instead.
            q.xw *= rot(uSlice.y);
            q.yw *= rot(uSlice.z);
            q.zw *= rot(uSlice.w);

            // Inside out. Every point is pushed to k times itself, k falling off as the square of how far
            // out it was, so the whole infinite grid is brought into a ball around the origin.
            //
            // Both ends of that are clamped, and they have to be. An inversion has a singularity at the
            // origin: a ray passing near it is magnified without limit, which shrinks the lattice it is
            // walking through to nothing, which shrinks its own step to nothing — so it stops, one cell
            // fills the screen, and the picture is a coloured blob. The clamp is the whole difference
            // between an inversion and a hang.
            swell = clamp(pull / max(dot(q, q), 0.6), 0.15, 6.0);
            q *= swell;

            // Travelling: the grid is moved past the camera rather than the camera through the grid,
            // because after an inversion there is no "through" to fly.
            q -= uSlice.x * 0.35;

            vec4 cell = q / size;
            vec4 home = round(cell);
            cellId = home.xyz;

            vec4 away = (cell - home) * size;
            offset = away.xyz;
            vec4 a = abs(away);

            // The wires along the cell's four axes: a wire along x is measured by how far the point is in
            // the other three. In four dimensions there are four of them, and the fourth is one a slice
            // can only ever catch the cross-section of — which is the cell that swells and closes.
            float wires = min(min(length(a.yzw), length(a.xzw)), min(length(a.xyw), length(a.xyz))) - wire;
            float knot = length(a) - bead;

            // Divided by the magnification, or the marcher steps by a distance that is not one and walks
            // straight through the grid it is meant to be finding.
            return min(wires, knot) / swell;
        }

        // A cell's own colour. Hashed from which cell it is, so a cell keeps its colour as it moves and
        // the picture is a lattice of coloured lights rather than a rainbow smeared over depth.
        vec3 cellColour(vec3 id, float swell, float sheen)
        {
            float h = fract(sin(dot(id, vec3(12.989, 78.233, 37.719))) * 43758.5453);
            vec3 phase = uPalette < 0.5  ? vec3(0.15, 0.45, 0.75)
                       : uPalette < 1.5  ? vec3(0.95, 0.30, 0.60)
                       : uPalette < 2.5  ? vec3(0.40, 0.70, 0.15)
                                         : vec3(0.05, 0.35, 0.65);

            // The magnification goes into the hue as well as the light: cells brought in from far away
            // are a different colour from the ones that were always close, which is the only depth cue a
            // picture with no shading and no fog has got.
            // …and the angle it is being seen at goes in as well, which is what makes it iridescent
            // rather than merely coloured: a wire changes hue along its own length as the eye travels
            // over it, the way oil on water does, because the colour depends on where you are standing.
            float shift = h * 0.35 + 0.12 * log2(max(swell, 0.05)) + 0.28 * sheen;

            // Pushed away from grey: a cosine ramp at half amplitude never leaves the middle of the
            // colour space, and a lattice of pastel wires is a lattice nobody looks at twice.
            return clamp(0.5 + 0.72 * cos(TAU * (shift + phase)), 0.0, 1.0);
        }

        void main()
        {
            vec2 uv = (gl_FragCoord.xy - 0.5 * uResolution) / uResolution.y;

            float beats = uBeat.x, drop = uBeat.z;
            int steps = int(uSteps);

            vec3 ro = vec3(0.0, 0.0, -3.0);
            vec3 rd = normalize(vec3(uv, uCamera.y));
            rd.xy *= rot(uCamera.x * 0.5);

            float dither = fract(sin(dot(gl_FragCoord.xy, vec2(11.317, 57.909)) + beats) * 39181.3717);

            float t = dither * 0.2;
            vec3 col = vec3(0.0);

            for (int i = 0; i < 96; i++)
            {
                if (i >= steps) break;

                float swell;
                vec3 id, offset;
                float d = grid(ro + rd * t, swell, id, offset);
                float sheen = dot(normalize(offset + 1e-5), rd);
                // A floor in WORLD units, not in the field's: near the inversion's centre the field
                // reports distances that are true but useless, and a ray that steps by them never arrives
                // anywhere. This is what keeps the march moving through the middle of the picture.
                float stride = max(abs(d), 0.02) * 0.75;

                // Gathered, never stopped: this is a lattice of light and a ray that stopped at the first
                // wire would show one wire.
                //
                // Of the three marched visualizers this is the only one that flashes on the beat, and it
                // is the only one whose source does: the grid is struck on each beat and falls away
                // before the next, and the slice it is cut at steps at the same moment. The other two fly
                // at a constant speed and change by cutting or by crossfading, so putting this on all
                // three made three different pictures read as one.
                // Steady. This one used to be struck on every beat and fall away before the next, which
                // is what its source does — but its source is one shader against one piece of music, and
                // sixty minutes of a whole visualizer throbbing at the tempo is not the same proposition.
                // The music is in the SHAPE instead: the kick swells the beads, the middle thickens the
                // wires, and the top end decides how hard the space is turned inside out. The lattice
                // breathes and opens rather than flashing.
                // Attenuated with distance as it is gathered. Without it every ray brings back the whole
                // depth of the lattice and the picture has no dark in it anywhere — which on a screen this
                // size is not vivid, it is flat: there is nothing for the colour to be bright against.
                col += cellColour(id, swell, sheen)
                     * (stride * (0.026 / (0.004 + d * d)))
                     * (1.0 + 0.5 * drop) * exp(-t * 0.055);

                t += stride;
            }

            float level = 0.35 + 1.15 * (uBands.x * 0.4 + uBands.y * 0.3 + uBands.z * 0.3);
            col *= level * 1.05;

            fragColor = vec4(col / (1.0 + col), 1.0);
        }
        """;

    protected override void CacheUniforms(GL gl, uint program) =>
        Where(gl, program, "uResolution", "uCamera", "uSlice", "uRoom2", "uBeat", "uBands", "uPalette",
            "uSteps");

    protected override void Upload(GL gl, in VizShot shot, int width, int height, int steps)
    {
        var room = Blend(shot.Was, shot.Now, shot.Blend);

        // Where the slice is cut, and how far the grid has been turned in the three planes that involve the
        // fourth axis. This is the plain beat count, not a stepped value. The same value also moves the grid
        // past the camera. If it were eased, the flight would jerk on every beat and then coast before the
        // next one. The turns are slow, because a four-dimensional rotation changes every cell at once. A
        // fast turn would be too hard to read.
        float w = shot.Beats * 0.22f;
        // The turns are smooth, and they are nothing else. The roll of the shot is a camera change, and it is
        // already applied to the ray. If it were added here, the whole four-dimensional grid would turn on every
        // cut. That looks like the picture being replaced, not like the camera moving.
        Set4(gl, "uSlice", w, shot.Beats * 0.010f, shot.Beats * 0.007f, shot.Beats * 0.004f);

        Set2(gl, "uResolution", width, height);
        Set3(gl, "uCamera", shot.Orbit, shot.Fov, shot.Sway);
        // What the music moves. Nothing here changes how bright the picture is.
        Set4(gl, "uRoom2",
            0.85f + room.Ring * 0.15f,                        // the cell
            room.Girth * (0.35f + 0.55f * Mid),               // the wire thickens with the middle
            room.Girth * (0.45f + 0.85f * Bass) * (1f + room.Cross * 0.4f),   // the bead swells with the kick
            3.2f + 2.4f * Air);                               // and the air opens the whole space out
        Set3(gl, "uBeat", shot.Beats, shot.Pulse, shot.Drop);
        Set3(gl, "uBands", Bass, Mid, Air);
        Set1(gl, "uPalette", shot.Palette);
        Set1(gl, "uSteps", steps);
    }
}
