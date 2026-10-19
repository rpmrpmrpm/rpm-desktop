using RPM.Core.Viz;
using Silk.NET.OpenGL;

namespace RPM.Desktop.Presentation.Viz.Controls;

/// <summary>
/// A concrete tunnel with the lights let into it, flown to the record.
///
/// <para>Unlike the other two marched visualizers, this one has solid surfaces. Rays stop at what they hit,
/// and a normal is taken there. What comes back is the wall's own dull grey, plus any light that reaches it.
/// That light is only ever the strips, because there is no other lamp in here. Then the ray continues once,
/// reflected, so the floor and the slabs hold the strips the way wet concrete does. A tunnel of glowing wire
/// would look completely different. This one is mostly dark, and its light is in some places rather than
/// everywhere.</para>
///
/// <para>The structure is a cylinder with slabs set across it. Each slab is turned by its own amount, and it
/// carries a strip along its edge. The spectrum sets which strips are lit and how bright they are. The bass
/// drives the strips nearest the camera, and the treble drives the ones furthest away. So a bass line comes
/// towards you, and a hi-hat sparkles away down the tunnel.</para>
///
/// <para>The cutting, the flight and the timing come from <see cref="MarchedStage"/>.</para>
/// </summary>
public sealed partial class TunnelStage : MarchedStage
{
    protected override float Bloom => 0.8f + 0.6f * Bass;

    protected override float Saturate => 1.40f;

    protected override string MarchSource(string versionDef) => $$"""
        {{versionDef}}
        precision highp float;

        uniform vec2  uResolution;
        uniform float uTravel;    // how far down the tunnel the camera has flown, in units
        uniform vec3  uCamera;    // orbit, fov, sway
        uniform vec3  uRoom1;     // folds, arm, twist
        uniform vec3  uRoom2;     // ring, girth, bite
        uniform vec3  uBeat;      // beats, pulse, drop
        uniform vec3  uBands;     // bass, mid, air
        uniform float uPalette;
        uniform float uSteps;

        out vec4 fragColor;

        const float TAU = 6.28318530718;

        mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, s, -s, c); }

        // One number in 0..1 per slab. Integer in, so a slab keeps its own angle and its own lamp for as
        // long as it is on screen rather than shimmering as the camera moves.
        float slabHash(float n, float salt)
        {
            return fract(sin(n * 41.317 + salt * 7.913) * 24634.6345);
        }

        // Value noise, and three octaves of it. This is where the tunnel's surface comes from: there is
        // no image anywhere in this visualizer, so the metal has to be generated at the point the ray
        // lands on it, which also means it never repeats and never blurs however close the camera gets.
        float valueNoise(vec3 p)
        {
            vec3 cell = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);          // smooth, so the lattice of the noise does not show

            // The eight corners of the cell, hashed and blended.
            float n = 0.0;
            for (int i = 0; i < 8; i++)
            {
                vec3 corner = vec3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
                float h = fract(sin(dot(cell + corner, vec3(127.1, 311.7, 74.7))) * 43758.5453);
                vec3 w = mix(1.0 - f, f, corner);
                n += h * w.x * w.y * w.z;
            }
            return n;
        }

        // Two octaves, not three. The third carries a seventh of the amplitude and a third of the cost
        // of the whole function, and this is called five times per pixel — it is the single most
        // expensive thing in the visualizer and it was buying detail below the size of a pixel.
        float grain(vec3 p)
        {
            return 0.65 * valueNoise(p) + 0.35 * valueNoise(p * 2.07);
        }

        float box(vec3 p, vec3 b)
        {
            vec3 q = abs(p) - b;
            return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
        }

        // The tunnel, and how bright the nearest thing to p is. Distance comes back; `lamp` comes back
        // as 0 for machinery and 1 for something lit, which is what the shading reads to know where the
        // light is.
        //
        // Nothing here ever closes the bore off. The camera is flying down the middle of it at speed, and
        // a slab across the way is not a dramatic moment, it is a grey frame and then another grey frame.
        float room(vec3 p, out float lamp)
        {
            float bore = uRoom1.y * 2.4;          // the tunnel's radius
            float pitch = uRoom2.x * 2.6;         // how far apart the ribs are
            float thick = uRoom2.y * 4.0;
            float r = max(length(p.xy), 1e-4);
            float ang = atan(p.y, p.x);

            // The wall, from inside.
            float tube = bore - r;

            // One cell per rib, and the cell's own number decides what is in it.
            float id = floor(p.z / pitch + 0.5);
            float qz = p.z - id * pitch;

            // A rib standing proud of the wall.
            float rib = max(bore * 0.82 - r, abs(qz) - thick * (0.6 + slabHash(id, 2.0)));

            // The machinery on the wall. The whole surface is cut into a fine grid of cells around and
            // along, and each cell is a block standing in from the wall by its own amount — so the tunnel
            // is not a smooth pipe with things attached to it, it is built out of parts all the way down,
            // which is what makes it read as machinery rather than as a drain.
            float around = 26.0;
            float sector = TAU / around;
            float run = pitch * 0.25;

            float column = floor((ang + 3.14159265) / sector);
            float row = floor(p.z / run + 0.5);
            float hash = slabHash(column * 7.0 + row * 13.0, 9.0);
            float tall = slabHash(column * 3.0 + row * 29.0, 17.0);

            // Local coordinates inside one block: across the wall, and along the tunnel.
            float across = (mod(ang + 3.14159265 + 0.5 * sector, sector) - 0.5 * sector) * r;
            float along = p.z - row * run;

            // How far in from the wall this block's face sits. A few of them are recesses rather than
            // blocks, which is what stops a grid this regular from reading as a pattern.
            float stand = bore * (0.045 + 0.16 * hash * tall);
            float face = bore - stand;

            vec3 slot = vec3(max(face - r, r - bore),
                             abs(across) - sector * r * (0.30 + 0.14 * tall),
                             abs(along) - run * (0.26 + 0.16 * hash));
            float block = max(slot.x, max(slot.y, slot.z));

            // Conduits: every so often a block is a lamp instead, and the lit ones are the only thing in
            // here with a colour of its own.
            float lit = step(0.93, hash);

            // The ring let into the wall between the ribs.
            float strip = max(abs(r - bore * 0.93) - thick * 0.30, abs(qz - pitch * 0.5) - thick * 0.55);
            float ringLit = step(0.42, slabHash(id, 3.0));

            // A fin swinging out from the wall, well clear of the middle, so there is something with an
            // edge on it to catch the light as the camera goes by.
            vec3 q = p;
            q.z = qz;
            q.xy *= rot(slabHash(id, 1.0) * TAU + uBeat.x * 0.02 * (slabHash(id, 5.0) - 0.5));
            vec3 fin = abs(q - vec3(bore * 0.62, 0.0, 0.0)) - vec3(bore * 0.30, thick * 1.6, thick * 0.9);
            float blade = length(max(fin, 0.0)) + min(max(fin.x, max(fin.y, fin.z)), 0.0);

            float d = min(min(tube, rib), min(min(strip, blade), block));

            // Lit only where the lit thing is the nearest thing: on the machinery beside it this reads 0,
            // which is what keeps the metal dark and the conduits bright.
            lamp = max(ringLit * (1.0 - smoothstep(0.0, thick * 0.5, strip - d)),
                       lit * (1.0 - smoothstep(0.0, thick * 0.4, block - d)));
            return d;
        }

        float room(vec3 p) { float ignored; return room(p, ignored); }

        // The surface normal, from four samples around the point rather than six. Half the cost of the
        // textbook central difference and no worse to look at on a wall this dull.
        vec3 normalAt(vec3 p)
        {
            vec2 e = vec2(0.0025, -0.0025);
            return normalize(e.xyy * room(p + e.xyy) + e.yyx * room(p + e.yyx)
                           + e.yxy * room(p + e.yxy) + e.xxx * room(p + e.xxx));
        }

        // The strips' colour for this section, and the band that drives them. The bottom end lights what
        // is near, the top end what is far, which is why a kick arrives and a hi-hat recedes.
        vec3 lampColour(float depth)
        {
            // Two of the four sections are lit the colour a machine room is lit: sick green, which is
            // also the only colour that reads on metal this dark. The other two are the cold blue and the
            // hot magenta a tunnel like this is usually given.
            vec3 cold = uPalette < 1.5 ? vec3(0.12, 0.85, 0.30) : uPalette < 2.5 ? vec3(0.20, 0.62, 1.00)
                                                                                : vec3(0.95, 0.35, 0.55);
            // The hot end is not white. A lamp that goes to white when it is loud is a lamp with no
            // colour left at exactly the moment it is filling the screen.
            vec3 hot  = uPalette < 1.5 ? vec3(0.45, 1.00, 0.35) : uPalette < 2.5 ? vec3(0.50, 0.85, 1.00)
                                                                                : vec3(1.00, 0.50, 0.35);
            // `close`, not `near`: near and far are reserved by the HLSL that ANGLE compiles to on
            // Windows, so a local of that name builds here and fails there.
            float close = exp(-depth * 0.06);
            float band = mix(uBands.z, uBands.x, close);
            return mix(cold, hot, clamp(band * 1.6, 0.0, 1.0)) * (0.25 + 2.6 * band);
        }

        // Marches until something is hit. Returns the distance travelled, or a negative number when the
        // ray left the tunnel without hitting anything.
        float travel(vec3 ro, vec3 rd, int steps, float from)
        {
            float t = from;
            for (int i = 0; i < 96; i++)
            {
                if (i >= steps) break;
                float d = room(ro + rd * t);
                if (d < 0.0025) return t;
                if (t > 90.0) break;
                t += d * 0.85;
            }
            return -1.0;
        }

        void main()
        {
            vec2 uv = (gl_FragCoord.xy - 0.5 * uResolution) / uResolution.y;

            float beats = uBeat.x, pulse = uBeat.y, drop = uBeat.z;
            int steps = int(uSteps);

            // Motion blur, cheaply: the frame is one sample per pixel, so the travel is jittered by up to
            // a fraction of a beat's worth per pixel instead. What a path tracer gets from forty samples
            // at forty slightly different instants, this gets from one sample at a scattered instant.
            float smear = fract(sin(dot(gl_FragCoord.yx, vec2(31.71, 19.37)) + beats) * 17631.417);
            vec3 ro = vec3(sin(beats * 0.13) * uCamera.z, cos(beats * 0.09) * uCamera.z,
                           uTravel + smear * uCamera.z * 0.0 + smear * 0.35);
            vec3 rd = normalize(vec3(uv, uCamera.y));
            rd.xy *= rot(uCamera.x * 0.4 + sin(beats * 0.07) * uCamera.z * 0.25);

            // Scattering the first step by pixel is what stands in for motion blur and for the grain a
            // path tracer gets for free: without it the step size shows up as rings around the vanishing
            // point, and every edge in here is a hard one.
            float dither = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233)) + beats) * 43758.5453);

            vec3 col = vec3(0.0);
            float t = travel(ro, rd, steps, 0.02 + dither * 0.12);

            if (t > 0.0)
            {
                vec3 p = ro + rd * t;
                float lamp;
                room(p, lamp);
                vec3 n = normalAt(p);

                // The metal, generated where the ray hit it. Four things over one another, none of them
                // an image: the cast of the alloy itself, panel seams cut into it, a dirt that runs the
                // length of the tunnel rather than around it, and a bolt at the corner of every panel.
                //
                // How much of that is worked out depends on how far away the wall is. Detail beyond about
                // thirty units is smaller than a pixel of a half-resolution buffer, so it costs a fifth of
                // the frame to compute a texture nobody can see; the relief goes first, then the cast.
                float detail = 1.0 - smoothstep(14.0, 30.0, t);

                vec3 skin = p * 2.7;
                float alloyCast = detail > 0.02 ? grain(skin) : 0.5;
                float dirt = grain(vec3(p.xy * 0.9, p.z * 0.16));           // stretched: it runs, it does not spot
                float seam = max(abs(fract(p.z * 3.1) - 0.5), abs(fract(atan(p.y, p.x) * 5.7) - 0.5));
                float bolt = length(fract(vec2(p.z * 3.1, atan(p.y, p.x) * 5.7)) - 0.5);

                // Relief. The same noise displaces the normal, so the surface has direction as well as
                // colour — without this a texture on a curved wall reads as paint rather than as metal.
                if (detail > 0.25)
                {
                    float e = 0.02;
                    vec3 bump = vec3(grain(skin + vec3(e, 0.0, 0.0)) - alloyCast,
                                     grain(skin + vec3(0.0, e, 0.0)) - alloyCast,
                                     grain(skin + vec3(0.0, 0.0, e)) - alloyCast) / e;
                    n = normalize(n - (bump - n * dot(bump, n)) * 0.06 * detail);
                }

                float sky = 0.5 + 0.5 * dot(n, normalize(vec3(0.2, 0.7, -0.6)));

                vec3 alloy = mix(vec3(0.020, 0.026, 0.022), vec3(0.048, 0.052, 0.040), alloyCast);
                alloy *= 0.55 + 0.75 * mix(0.5, dirt, detail);                                 // stains down the length
                alloy *= 1.0 - 0.55 * smoothstep(0.40, 0.49, seam);          // the seams are cut, so they are dark
                alloy *= 1.0 - 0.35 * smoothstep(0.10, 0.05, bolt);          // and a bolt at each corner
                vec3 wall = alloy * (0.3 + 1.1 * sky);

                vec3 light = lampColour(t) * lamp;

                // One bounce. A strip reflected in the wall is most of what makes a tunnel like this read
                // as concrete rather than as paper, and one bounce is enough because the only thing worth
                // reflecting is a lamp.
                // …and only from a wall the camera is actually near. A reflection thirty units down the
                // tunnel is a few dim pixels, and it costs a second march to find out.
                vec3 rr = reflect(rd, n);
                float t2 = t < 26.0 ? travel(p + n * 0.02, rr, steps / 3, 0.02) : -1.0;
                if (t2 > 0.0)
                {
                    vec3 p2 = p + n * 0.02 + rr * t2;
                    float lamp2;
                    room(p2, lamp2);
                    light += lampColour(t + t2) * lamp2 * 0.85 * exp(-t2 * 0.08);
                }

                col = wall + light;

                // No per-beat swell. This one is cut, not pulsed: the camera holds a shot for two bars
                // and changes on a beat, which is where its rhythm is. Brightening the whole frame on
                // every beat as well is a second rhythm on top of the first, and with three visualizers
                // doing it they all read as the same visualizer.
                col *= 0.9 + 0.5 * drop;
                col *= exp(-t * 0.035);   // the far end goes to nothing, which is where the tunnel ends
            }

            // Range-compressed on the way into the buffer, expanded by the screen pass. Eight bits of a
            // bloomed picture would clip everything the bloom is made of.
            fragColor = vec4(col / (1.0 + col), 1.0);
        }
        """;

    protected override void CacheUniforms(GL gl, uint program) =>
        Where(gl, program, "uResolution", "uTravel", "uCamera", "uRoom1", "uRoom2", "uBeat", "uBands",
            "uPalette", "uSteps");

    protected override void Upload(GL gl, in VizShot shot, int width, int height, int steps)
    {
        var room = Blend(shot.Was, shot.Now, shot.Blend);

        Set2(gl, "uResolution", width, height);
        // The distance flown, not the speed multiplied by the time. Multiplying would make the picture lurch
        // every two bars. A cut picks a new speed, and the whole journey is then rescaled by it. See VizShot.Travelled.
        Set1(gl, "uTravel", shot.Travelled * 1.6f);
        Set3(gl, "uCamera", shot.Orbit, shot.Fov, shot.Sway);
        Set3(gl, "uRoom1", shot.Now.Folds, room.Arm, room.Twist);
        Set3(gl, "uRoom2", room.Ring, room.Girth, room.Cross);
        Set3(gl, "uBeat", shot.Beats, shot.Pulse, shot.Drop);
        Set3(gl, "uBands", Bass, Mid, Air);
        Set1(gl, "uPalette", shot.Palette);
        Set1(gl, "uSteps", steps);
    }
}
