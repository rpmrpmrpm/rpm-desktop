using RPM.Core.Viz;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// Filigree, seen from inside it.
///
/// <para>Space is folded around the axis, turned, folded again at a finer count, and then cut into cells
/// along the flight. These are three folds one after another. So a single bar with a cross taken out of it
/// becomes a tunnel of tracery too fine to count. The camera is not outside looking at an ornament. It is
/// inside the ornament, and the ornament closes around it.</para>
///
/// <para>Nothing is shaded. Rays are stepped by their distance to the metal, on whichever side of it they
/// are. So they pass straight through and keep going. What comes back is the light gathered on the way.
/// That makes it look like glass and mist rather than a solid. There are no faces here. There is only how
/// close each ray came, and how often.</para>
///
/// <para>The colour is a wash first and a picture second. It is cream in the middle of the screen and deep
/// indigo at the corners. The gathered light is laid over the top of it. The result is warm and hazy, where
/// the tunnel is dark and the lattice is cold.</para>
/// </summary>
public sealed partial class KaleidoscopeStage : MarchedStage
{
    protected override float Bloom => 0.20f + 0.35f * Mid;

    protected override float Saturate => 1.55f;

    protected override string MarchSource(string versionDef) => $$"""
        {{versionDef}}
        precision highp float;

        uniform vec2  uResolution;
        uniform float uTravel;
        uniform vec3  uCamera;    // orbit, fov, sway
        uniform vec3  uRoom1;     // folds, arm, twist
        uniform vec3  uRoom2;     // ring, girth, bite
        uniform vec3  uBeat;      // beats, glitch, drop
        uniform vec3  uShape;     // the section's solid, the one before it, and how far between
        uniform float uPalette;
        uniform vec3  uBands;     // bass, mid, air
        uniform float uSteps;

        out vec4 fragColor;

        const float TAU = 6.28318530718;

        mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }

        // Fold the plane into `count` identical wedges about the origin. Everything drawn after a fold is
        // drawn that many times, and folds compose: two of them at different counts, with a turn in
        // between, give a count that is neither of them and a figure that is not a repeat of either.
        vec2 wedge(vec2 p, float count)
        {
            float sector = TAU / count;
            float a = mod(atan(p.y, p.x) + 0.5 * sector, sector) - 0.5 * sector;
            return vec2(cos(a), sin(a)) * length(p);
        }

        float box(vec3 p, vec3 b)
        {
            vec3 q = abs(p) - b;
            return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
        }

        // A regular prism: fold the plane into `sides` wedges, and one straight cut across the wedge is
        // then all `sides` faces at once. Three gives a triangle, six a hexagon, and a large number is
        // indistinguishable from a cylinder.
        // `deep` rather than `half`: half is a reserved word in GLSL ES, kept for a precision qualifier
        // that was never added, and a parameter of that name will not compile on ANGLE.
        float prism(vec3 p, float sides, float across, float deep)
        {
            float sector = TAU / sides;
            float a = mod(atan(p.y, p.x) + 0.5 * sector, sector) - 0.5 * sector;
            float face = cos(a) * length(p.xy) - across;
            return max(face, abs(p.z) - deep);
        }

        // The eight-sided solid, which is the one shape here with no faces parallel to anything.
        float octa(vec3 p, float s)
        {
            return (abs(p.x) + abs(p.y) + abs(p.z) - s) * 0.57735;
        }

        // What the mirrors are shown. Four solids, one per section, and a section slides into the next
        // through the distances themselves rather than by dissolving between two pictures — a bar grows
        // three faces, then six, then it is a diamond.
        float seed(vec3 p, float which, float girth, float arm)
        {
            if (which < 0.5) return box(p, vec3(girth * 1.5, arm * 0.22, girth * 1.5));
            if (which < 1.5) return prism(p, 3.0, girth * 2.0, arm * 0.22);
            if (which < 2.5) return prism(p, 6.0, girth * 1.8, arm * 0.24);
            return octa(p, girth * 2.6);
        }

        // The three axis-aligned bars through a cell, as one distance.
        float cutout(vec3 p, float r)
        {
            vec3 q = abs(p);
            return min(max(q.x, q.y), min(max(q.y, q.z), max(q.x, q.z))) - r;
        }

        float filigree(vec3 p)
        {
            float folds = uRoom1.x, arm = uRoom1.y, twist = uRoom1.z;
            float ring = uRoom2.x, girth = uRoom2.y, bite = uRoom2.z;

            // First fold, about the axis, on a space that is already corkscrewing. The shot's roll is
            // NOT in here: rolling the world rather than the camera spins the room bodily around the
            // viewer on every cut, which is not a cut, it is a lurch. The roll belongs on the ray.
            p.xy *= rot(p.z * twist);
            p.xy = wedge(p.xy, folds);
            p.x -= arm;

            // Turn again, by a different amount, and fold at twice the count. This is where the tracery
            // comes from: the second fold is folding something that is already folded, so the figure is
            // multiplied rather than repeated.
            p.xy *= rot(p.z * twist * 0.6);
            p.xy = wedge(p.xy, folds * 2.0);
            p.x -= arm * 0.42;

            // Mirror the pair, which halves everything again and puts a seam down the middle of it.
            p.xy = abs(p.xy) - vec2(arm * 0.16);

            // Cells along the flight, and long ones. Measured over the volume a ray actually crosses,
            // cells at the room's own spacing leave a twelfth of the space within a twentieth of a unit
            // of metal — which is to say no space at all: every ray threads tracery for its whole length,
            // every ray gathers the same light, and the picture is an even fog whatever the exposure.
            p.z = mod(p.z + 1.1 * ring, 2.2 * ring) - 1.1 * ring;

            // The section's solid, with a cross cut out of it: what is left is edges, and edges are all
            // that survives being folded this many times — a solid would close the tunnel up.
            float solid = mix(seed(p, uShape.y, girth, arm), seed(p, uShape.x, girth, arm), uShape.z);
            return max(solid, -cutout(p, girth * 0.85 * bite));
        }

        void main()
        {
            vec2 uv = (gl_FragCoord.xy - 0.5 * uResolution) / uResolution.y;

            float beats = uBeat.x, glitch = uBeat.y, drop = uBeat.z;
            int steps = int(uSteps);

            // The break. For the fraction of a beat after the room changes, the picture is quantised into
            // blocks and the rows are pulled sideways — the same thing that happens to a video signal when
            // something upstream of it changes. It is the only cut this visualizer makes: everything else
            // about it crossfades, so without this a change of room arrives without being noticed.
            if (glitch > 0.0)
            {
                float blocks = mix(140.0, 26.0, glitch);
                uv = floor(uv * blocks) / blocks;
                float band = floor(uv.y * 22.0);
                uv.x += (fract(sin(band * 91.7 + floor(beats)) * 4713.31) - 0.5) * glitch * 0.35;
            }

            vec3 ro = vec3(sin(beats * 0.11) * uCamera.z * 0.5, cos(beats * 0.08) * uCamera.z * 0.5, uTravel);
            vec3 rd = normalize(vec3(uv, uCamera.y));
            rd.xy *= rot(uCamera.x * 0.5 + sin(beats * 0.05) * uCamera.z * 0.2);

            float dither = fract(sin(dot(gl_FragCoord.xy, vec2(21.577, 43.191)) + beats) * 24634.6345);

            float t = dither * 0.3;
            float core = 0.0;   // the tight layer: the edges themselves
            float haze = 0.0;   // the loose one: the air they are standing in

            for (int i = 0; i < 96; i++)
            {
                if (i >= steps) break;

                // Whichever side of the metal the ray is, it is stepped by how far it is from it — so it
                // passes through and keeps gathering.
                //
                // The floor on the step is what decides how far down the tunnel the picture reaches, and
                // it is not a detail: this field is folded three times over, so close to the tracery it
                // reports distances of a few thousandths, and a ray stepping by those spends every step
                // it has in the first few units and the whole screen comes back the same shade of fog.
                float d = max(abs(filigree(ro + rd * t)), 0.006);

                // Counted per STEP, not per unit travelled, and that is the whole look. The step is the
                // distance to the metal, so a ray running through open space crosses it in a handful of
                // long strides and picks up almost nothing, while a ray threading the tracery takes
                // dozens of tiny ones and lights up. Weight these by the stride and the integral is
                // honest, every ray gathers about the same amount, and the picture is an even fog.
                // Steeply. The tracery goes on for as far as a ray can travel, so at a gentle falloff
                // every pixel is the sum of sixty units of it and the median of the picture ends up level
                // with its highlights. What is near has to be worth several times what is far, or there
                // is no depth in it and no black in it.
                float air = exp(-t * 0.11);
                // A tight kernel and a loose one. The tight one has to be TIGHT: at a wide epsilon every
                // ray that passes anywhere near the tracery scores nearly as well as one that threads it,
                // the median of the picture climbs to meet its peaks, and what is left is an even wash
                // with no blacks in it and no highlights either.
                core += (0.0022 / (0.0022 + d * d)) * air;
                haze += (0.004 / (0.260 + d * d)) * air;

                t += d * 0.6;
                if (t > 70.0) break;   // out of the tracery and into the open: nothing more to gather
            }

            // The wash: cream where the eye is, indigo at the corners. It is laid down first and the
            // gathered light goes over it, which is what gives the picture air instead of a black ground.
            float fall = clamp(length(uv) * 1.20, 0.0, 1.0);
            // Each section has its own pair: what the tracery is made of, and what the air behind it is.
            // Four of them, on the section's own palette, so a phrase changes the colour of the room at
            // the same moment it changes the shape in it.
            vec3 lit  = uPalette < 0.5 ? vec3(1.00, 0.76, 0.30)   // sand
                      : uPalette < 1.5 ? vec3(1.00, 0.42, 0.60)   // rose
                      : uPalette < 2.5 ? vec3(0.35, 1.00, 0.68)   // jade
                                       : vec3(0.45, 0.76, 1.00);  // ice
            vec3 dark = uPalette < 0.5 ? vec3(0.09, 0.07, 0.16)
                      : uPalette < 1.5 ? vec3(0.14, 0.05, 0.12)
                      : uPalette < 2.5 ? vec3(0.04, 0.12, 0.11)
                                       : vec3(0.05, 0.08, 0.18);

            vec3 wash = mix(lit * 0.9, dark, fall);

            float level = 0.35 + 1.25 * (uBands.x * 0.35 + uBands.y * 0.45 + uBands.z * 0.20);
            // No per-beat swell: this one flies at a constant speed and changes by crossfading its
            // room, which is what its source does. The beat is in what the tracery IS, not in a light
            // being turned up and down on top of it.
            float strike = 0.85 + 0.5 * drop;

            // The tracery keeps its own colour whatever the music is doing; the air band only takes it
            // toward white. Mixing it a long way toward a COMPLEMENTARY colour sounds reasonable and is
            // not: two opposite hues in equal parts are grey, and grey is what the picture then is.
            vec3 metal = mix(lit, vec3(1.00, 1.00, 1.00), clamp(uBands.z * 0.7, 0.0, 0.30));

            // The floor has to be far lower than it looks: this is linear light and the screen pass
            // gamma-encodes it, which lifts a hundredth to a seventh. A wash of 0.012 is not a hint of
            // colour in the dark, it is a grey card behind the whole picture.
            vec3 col = wash * (0.0025 + 0.004 * haze * level)
                     + metal * core * 0.045 * level * strike;

            fragColor = vec4(col / (1.0 + col), 1.0);
        }
        """;

    protected override void CacheUniforms(GL gl, uint program) =>
        Where(gl, program, "uResolution", "uTravel", "uCamera", "uRoom1", "uRoom2", "uBeat", "uShape",
            "uPalette", "uBands", "uSteps");

    /// <summary>Which of the four solids a room is built from. The room's cross-cut picks it. That is a number
    /// the section already has, so no separate hash is needed.</summary>
    private static float Solid(float cross) => MathF.Floor(MathF.Min(3.99f, (cross - 0.25f) / 0.95f * 4f));

    protected override void Upload(GL gl, in VizShot shot, int width, int height, int steps)
    {
        var room = Blend(shot.Was, shot.Now, shot.Blend);

        Set2(gl, "uResolution", width, height);
        // The distance flown. The flight speed is constant, and it does not jump when the shot changes
        // speed. See VizShot.Travelled.
        Set1(gl, "uTravel", shot.Travelled * 1.3f);
        Set3(gl, "uCamera", shot.Orbit, shot.Fov, shot.Sway);
        Set3(gl, "uRoom1", shot.Now.Folds, room.Arm, room.Twist);
        Set3(gl, "uRoom2", room.Ring, room.Girth, room.Cross);
        // The glitch fires when a section arrives, and on a drop. It is read from the blend, not counted from
        // the beats. The blend is zero at the first beat of a section and one once it has settled. The blend
        // also knows how long a bar is in this track, which a fixed count of thirty-two beats does not.
        float glitch = Math.Max(
            shot.Blend < 0.25f ? 1f - shot.Blend * 4f : 0f,
            shot.Drop > 0.85f ? (shot.Drop - 0.85f) * 6.6f : 0f);

        Set3(gl, "uBeat", shot.Beats, glitch, shot.Drop);

        // Which solid the mirrors show, for this section and the one before it. It is read from the room's own
        // numbers. So the shape changes on the same phrase as the fold count and the twist, and not on a
        // separate timer. Several things change together, and that is what makes the change easy to see.
        Set3(gl, "uShape", Solid(shot.Now.Cross), Solid(shot.Was.Cross), shot.Blend);
        Set1(gl, "uPalette", shot.Palette);
        Set3(gl, "uBands", Bass, Mid, Air);
        Set1(gl, "uSteps", steps);
    }
}
