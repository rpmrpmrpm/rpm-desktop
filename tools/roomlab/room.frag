#version 300 es
precision highp float;
// Its own line: in ES 3.00 a float precision does not cover sampler types, and a
// sampler2DArray without one is a compile error rather than a default.
precision highp sampler2DArray;

// The room the playlist header looks into: a box, open at the front, with four soft
// families of light filling its volume and five gradient faces around them.
//
// The light is MARCHED, not stacked. Sampling the Pegasus room on a handful of planes
// between the mouth and the back wall reads as a billboard smeared through depth
// rather than as something occupying the room: every plane carries the same centred
// picture, so they pile up in the middle whatever their spacing. A volume needs a
// field that answers to three coordinates, so the families below are the Pegasus
// ones lifted into three dimensions. This copy of the light is separate from the
// Pegasus one, so a change to one does not change the other.
//
// The faces are TINTS, not surfaces, and by default there are none: uWallAlpha is how
// much of a wall's own colour survives, and it starts at nought. The box is still
// what shapes the light: the march runs from the mouth to whichever wall the ray
// reaches, so a ray leaving through the floor gathers less than one crossing the
// room. But no face is drawn. What reads as a room is the light's own perspective.
//
// Nothing is laid over the top. The full-screen player dims one side of its room,
// veils it and grains it, because it writes a title across it; a header wants the
// room visible.

uniform vec2  uResolution;
uniform float uTime;

// The music, enveloped. There is no audio here, so these are held at the idle levels
// the landing page uses and moved by hand.
uniform float uBass;
uniform float uMid;
uniform float uAir;

// The light's four colours, as PegasusStage feeds them from a sleeve.
uniform vec3 uGround;
uniform vec3 uTint;
uniform vec3 uGlow;
uniform vec3 uShade;

// The box, in world units. The opening is the plane z = 0 and the back wall is at
// z = -uDepth; x runs across it and y up it, both centred on nought.
uniform float uWidth;
uniform float uHeight;
uniform float uDepth;

// Where the room is seen from. In front of the opening, so uCam.z is positive.
uniform vec3 uCam;

// How much of the opening the picture actually shows, as a fraction of it. At one the
// whole mouth is in frame and nothing in the room can be larger than the viewport. At
// less than one the frame is a window onto a bigger room: the walls, the light and
// the pictures all carry on past the edges, and a picture near the camera can be
// taller than the whole header.
//
// This is the control for that and the box's own dimensions are not, which is worth
// saying because it is not obvious: the rays are aimed through the opening, so making
// the box bigger scales the room and the view of it together and changes nothing.
uniform float uWindow;

// The five faces, in the order: 0 back, 1 floor, 2 ceiling, 3 left, 4 right. Each
// runs from uFaceA to uFaceB across itself. uFaceDir picks the axis the gradient runs
// along, 0 for u and 1 for v, and blends between them in between; uFacePow bends it,
// above one holding the A colour further across.
uniform vec3  uFaceA[5];
uniform vec3  uFaceB[5];
uniform float uFaceDir[5];
uniform float uFacePow[5];
// How much of a face is there at all. At nought the box is only its light.
uniform float uWallAlpha;

// The march. uDensity is how much light a unit of field carries, uAbsorb how much of
// what is behind it the light hides, and uSteps how finely the ray is cut.
//
// uReach is how far it runs, as a multiple of the box's diagonal. The march does NOT
// stop at the walls: clipping it there drew the box's silhouette in the light itself,
// a hard diagonal across the top and bottom corners that reads as a wall whether or
// not a wall is painted. Run past them and the light ends where the field ends, which
// is a falloff rather than an edge.
uniform int   uSteps;
uniform float uDensity;
uniform float uAbsorb;
uniform float uReach;
// How hard the families are driven before the ramps below are applied. A lobe spread
// over a third axis has fewer neighbours piling onto any one point than the same lobe
// on a plane, so the field has to be lifted to reach ramps that suit a flat field.
uniform float uFieldGain;
// How much a figure is stretched to the box's proportions. At nought it stays round,
// which is what a nebula is made of; at one it is drawn out to the room's own shape,
// which in a header four times as wide as it is tall turns every figure into a
// streak. Filling the room is the spread's job, not this one's.
uniform float uAniso;
uniform float uFieldScale;

// How much the light is pushed out against the walls and hollowed out of the middle.
// Measured across the room only, never along it: counting depth would make the point
// nearest the camera count as "out" and pile the light up right in front of the lens,
// which is the opposite of what this is for. The middle of the cross-section is where
// the pictures are, so that is what has to be cleared.
//
// In the middle it is cleared by DEPTH as well, so that what shows between the
// pictures is distance rather than light standing in front of them: near the window
// the middle carries almost nothing, and what is left of it starts well back and is
// dimmed. Against the walls neither applies and the light is at full strength at any
// depth.
//
//   uHollow  how much of it, nought leaving the light even through the room
//   uHollowEdge where the wall ramp starts, as a fraction of the way out to a wall
//   uBackFrom   how far into the room the middle's light begins
//   uCentreDim  how much of it is left there once it has begun
uniform float uHollow;
uniform float uHollowEdge;
uniform float uBackFrom;
uniform float uCentreDim;

// How the light is distributed through the room. If the figures orbited the origin,
// every one of them would sit in the middle of the box and the corners would stay
// dark whatever the mapping. A nebula is a thing you are inside, so each figure gets
// a place of its own in the volume and moves about THAT.
//
//   uLobes  how many figures each of the three families has
//   uSpread how far their places reach, as a fraction of the box; past one they carry
//           on outside the walls, which is what stops the light ending at them
//   uWobble how far a figure drifts from its place
//   uSize   a multiplier on every figure's radius
uniform int   uLobes;
uniform float uSpread;
uniform float uWobble;
uniform float uSize;

// The frames. Each one is a rectangle facing the camera, at a place in the room and
// with a size of its own; the nebula is marched up to the nearest one the ray meets
// and stops there, so a frame is fogged by whatever light stands in front of it and
// hides whatever stands behind.
//
// The positions and sizes are worked out on the page, not here, because they are the
// part the app needs: the room is prerendered but the pictures are not, so what has
// to come out of the lab is where each rectangle is.
// A frame is an ORIENTED rectangle, not one facing the camera: uFrameRight and
// uFrameUp are its own two axes, each already the length of the half-side it spans,
// so a frame standing off to one side is turned into the room and foreshortens like
// everything else in it. Camera-facing rectangles read as stickers on the picture.
uniform int       uFrames;
uniform vec3      uFramePos[10];    // centre, in world units
uniform vec3      uFrameRight[10];  // half-width along the frame's own across
uniform vec3      uFrameUp[10];     // half-height along the frame's own up
uniform float     uFrameLayer[10];  // which picture goes in it
uniform sampler2DArray uArt;
uniform float uFrameBorder;
uniform vec3  uFrameInk;
uniform float uFrameLight;
// How thick the frame's board is, in world units. A frame is a SLAB, not a plane: a
// flat front with the picture set in it, and a side that is seen wherever the frame is
// turned away from the camera. That side is the whole of what makes it read as an
// object rather than as an outline drawn on one, and it needs no bevel to do it.
uniform float uFrameThick;

uniform float uSpill;
uniform float uExposure;
uniform float uTone;
uniform float uDither;

// The third family's rates, from RoomSource. The counts are a control: a nebula
// is made of how many figures there are as much as of what they are.
const float AIR_RATE = 1.3;
const float AIR_SOFT = 1.08;
const float AIR_PEAK = 1.0 / AIR_SOFT;

// RoomSource's lobe, one dimension up. A figure with no edge at all: it never reaches
// zero, which is what lets several of them melt into one another instead of
// overlapping like coins. `soft` spreads the falloff, which blurs the lobe rather
// than enlarging it.
float rpmLobe3(vec3 p, vec3 c, float r, float soft)
{
    vec3 d = p - c;
    float r2 = r * r;
    return r2 / (dot(d, d) + r2 * soft);
}

// Where one figure lives when it is not moving, in the box's own half-extents.
//
// R3, the three-dimensional plastic-constant sequence, rather than a hash of the
// index. Hashed points clump and leave holes at these counts, and a hole in a nebula
// reads as a mistake rather than as space; R3 fills a volume about as evenly as
// anything can without knowing how many points are coming.
vec3 lobeHome(float i)
{
    const vec3 R3 = vec3(0.8191725133961645, 0.6710436067037893, 0.5497004779019703);
    return fract(R3 * (i + 0.5) + 0.5) * 2.0 - 1.0;
}

// The light at one point in the room: what colour it is, and how much of it there is.
// The three families are RoomSource's, with the same rates, the same golden-angle phases,
// the same lobe falloff and the same colour weighting, but spread through the volume
// rather than orbiting its middle.
// `home` is the box's half-extents in the field's own coordinates, so a figure placed
// at one of its corners sits in the corner of the room whatever the mapping does to
// its shape. That separation is the point: uAniso decides how round a figure is and
// uSpread decides how far they reach, and neither answers for the other.
vec4 roomField(vec3 p, vec3 home, float t, float bass, float mid, float air)
{
    // -- the third family --
    //
    // Smallest, quickest, in the sleeve's third colour alone.
    float t3 = t * AIR_RATE;
    vec3 w3 = p;
    w3 += 0.11 * vec3(sin(p.y * 3.2 + t3 * 0.29), cos(p.x * 2.9 - t3 * 0.25),
                      sin(p.z * 3.0 + t3 * 0.27));
    w3 += (0.05 + air * 0.11) * vec3(sin(w3.y * 5.1 - t3 * 0.21), cos(w3.x * 4.6 + t3 * 0.23),
                                     sin(w3.z * 4.8 - t3 * 0.22));

    float field3 = 0.0;
    for (int i = 0; i < 16; i++)
    {
        if (i >= uLobes) break;
        float fi = float(i);
        float a = t3 * (0.095 + fi * 0.026) + fi * 2.9;
        vec3 c = lobeHome(fi + 80.0) * home * uSpread
               + vec3(cos(a * 0.94 + fi * 1.9), sin(a * 1.37 + fi * 2.6),
                      cos(a * 0.71 + fi * 1.4)) * uWobble;
        float r = (0.085 + 0.030 * sin(t3 * 0.41 + fi * 3.1)) * (1.0 + air * 0.85) * uSize;
        field3 += rpmLobe3(w3, c, r, AIR_SOFT);
    }
    field3 *= uFieldGain;

    // The two ramps are fractions of AIR_PEAK, the height ONE lobe reaches, so the
    // family can be blurred without also being dimmed.
    float d3 = smoothstep(0.17 * AIR_PEAK, 1.22 * AIR_PEAK, field3);
    vec3 col3 = uShade * (d3 * 0.70
              + smoothstep(0.65 * AIR_PEAK, 1.73 * AIR_PEAK, field3) * (0.16 + air * 0.62));

    // -- the figures --
    //
    // Warp the volume twice before measuring anything. The lobes are round; this is
    // the whole of why the light is not. The second round is pushed by the bass, so
    // the low end does not merely swell the shapes, it stirs them.
    vec3 w = p;
    w += 0.20 * vec3(sin(p.y * 2.1 + t * 0.19), cos(p.x * 1.8 - t * 0.15),
                     sin(p.z * 2.0 + t * 0.17));
    w += (0.10 + bass * 0.14) * vec3(sin(w.y * 3.9 - t * 0.11), cos(w.x * 3.3 + t * 0.13),
                                     sin(w.z * 3.6 - t * 0.12));

    float field = 0.0;
    float weight = 0.0;
    vec3 wash = vec3(0.0);
    for (int i = 0; i < 16; i++)
    {
        if (i >= uLobes) break;
        float fi = float(i);
        // The golden angle spreads their phases so no two come round together.
        float a = t * (0.047 + fi * 0.0115) + fi * 2.39996;
        vec3 c = lobeHome(fi) * home * uSpread
               + vec3(cos(a + fi * 1.3), sin(a * 0.83 + fi * 2.1),
                      cos(a * 0.61 + fi * 1.7)) * uWobble;
        float r = (0.20 + 0.055 * sin(t * 0.21 + fi * 1.7)) * (1.0 + bass * 0.62) * uSize;
        float g = rpmLobe3(w, c, r, 0.42);
        field += g;
        // Weighted by the SQUARE of the lobe's strength. Averaged linearly, a dozen
        // overlapping lobes hand every point very nearly the same mixture and the
        // whole room comes out one colour; squared, the nearest lobe owns its own
        // neighbourhood and the colours stay separate where they meet.
        float wg = g * g;
        weight += wg;
        wash += mix(uTint, uGlow, fract(fi * 0.41 + 0.15)) * wg;
    }
    wash /= max(weight, 1e-4);
    field *= uFieldGain;

    // -- the second family --
    //
    // Smaller, quicker, in the counter-colour. Their drive is the MIDDLE of the
    // music, so they dart while the big lobes swell.
    vec3 w2 = p;
    w2 += 0.15 * vec3(sin(p.y * 2.6 - t * 0.27), cos(p.x * 2.3 + t * 0.23),
                      sin(p.z * 2.4 - t * 0.25));
    w2 += (0.07 + mid * 0.13) * vec3(sin(w2.y * 4.4 + t * 0.19), cos(w2.x * 3.9 - t * 0.21),
                                     sin(w2.z * 4.1 + t * 0.20));

    float field2 = 0.0;
    float weight2 = 0.0;
    vec3 wash2 = vec3(0.0);
    for (int i = 0; i < 16; i++)
    {
        if (i >= uLobes) break;
        float fi = float(i);
        float a = t * (0.083 + fi * 0.021) + fi * 1.7;
        vec3 c = lobeHome(fi + 40.0) * home * uSpread
               + vec3(cos(a * 1.21 + fi * 2.7), sin(a + fi * 0.9),
                      sin(a * 0.77 + fi * 1.5)) * uWobble;
        float r = (0.145 + 0.045 * sin(t * 0.37 + fi * 2.3)) * (1.0 + mid * 0.55) * uSize;
        // A wider skirt than the big lobes wear, which spreads these out instead of
        // leaving bright coins travelling through the room.
        float g = rpmLobe3(w2, c, r, 0.88);
        // ...and a gentler weighting than the square the primaries use: these want to
        // bleed into one another, not hold their own ground.
        float wg = g * sqrt(g);
        field2 += g;
        weight2 += wg;
        wash2 += mix(uShade, uGlow, fract(fi * 0.53 + 0.40)) * wg;
    }
    wash2 /= max(weight2, 1e-4);
    field2 *= uFieldGain;

    float d1 = smoothstep(0.30, 1.75, field);
    // A long, low ramp: the edge of the second family should never be findable.
    float d2 = smoothstep(0.20, 1.90, field2) * 0.78;

    // Where several lobes lie over one another the colour LIFTS rather than clipping,
    // and the low end lifts it further, which is the bloom that makes the room feel
    // lit from inside.
    vec3 emit = col3
              + wash  * (d1 + smoothstep(1.60, 3.40, field)  * (0.12 + bass * 0.44))
              + wash2 * (d2 + smoothstep(1.30, 3.50, field2) * (0.14 + bass * 0.40));

    return vec4(emit, max(d1, max(d2, d3 * 0.6)));
}

out vec4 fragColor;

// One face's colour at a point across it.
vec3 faceColour(int i, vec2 uv)
{
    float g = mix(uv.x, uv.y, uFaceDir[i]);
    g = pow(clamp(g, 0.0, 1.0), max(uFacePow[i], 0.001));
    return mix(uFaceA[i], uFaceB[i], g);
}

void main()
{
    vec2 uv = gl_FragCoord.xy / uResolution;

    float hw = uWidth * 0.5;
    float hh = uHeight * 0.5;
    float hd = uDepth * 0.5;

    // The ray is aimed through the OPENING rather than through a field of view, so the
    // crop is a stated fraction of the room rather than an angle that has to be
    // re-fitted every time the box changes. uWindow is that fraction; at one the whole
    // opening is in frame, below one the room carries on past the edges.
    // The window's HEIGHT is a fraction of the room's, and its width follows from the
    // picture's shape. Taking both from the box would stretch everything by the
    // difference between the box's proportions and the output's: a 4:1 box drawn into
    // a 4.377:1 header would make every face nine per cent too wide. The box's width
    // decides how much room there is sideways and has no effect on the crop.
    float wh = hh * uWindow;
    float ww = wh * (uResolution.x / max(uResolution.y, 1.0));
    vec3 mouth = vec3(mix(-ww, ww, uv.x), mix(-wh, wh, uv.y), 0.0);
    vec3 rd = normalize(mouth - uCam);

    // In from the window to the first wall. The start is inside the box, so this is
    // the exit half of the usual slab intersection.
    const float FAR = 1e9;
    float tx = rd.x > 0.0 ? (hw - mouth.x) / rd.x
             : rd.x < 0.0 ? (-hw - mouth.x) / rd.x : FAR;
    float ty = rd.y > 0.0 ? (hh - mouth.y) / rd.y
             : rd.y < 0.0 ? (-hh - mouth.y) / rd.y : FAR;
    float tz = rd.z < 0.0 ? (-uDepth - mouth.z) / rd.z : FAR;

    float tHit = min(tx, min(ty, tz));
    vec3 hit = mouth + rd * tHit;

    // Which face was hit, and where across it. v on the floor, the ceiling and the
    // two sides runs from the opening to the back wall, so their gradients can be
    // asked to run into the room.
    int face;
    vec2 fuv;
    float depth = clamp(-hit.z / max(uDepth, 1e-4), 0.0, 1.0);
    if (tHit == tz)
    {
        face = 0;
        fuv = vec2((hit.x + hw) / uWidth, (hit.y + hh) / uHeight);
    }
    else if (tHit == ty)
    {
        face = rd.y > 0.0 ? 2 : 1;
        fuv = vec2((hit.x + hw) / uWidth, depth);
    }
    else
    {
        face = rd.x > 0.0 ? 4 : 3;
        fuv = vec2(depth, (hit.y + hh) / uHeight);
    }

    vec3 wall = faceColour(face, fuv) * uWallAlpha;

    // The nearest frame the ray meets, if any. Each is a BOX: the picture's rectangle
    // grown by the border, and extruded backwards by the board's thickness. So the
    // test is a slab intersection in the frame's own axes, and what comes out of it is
    // both where on the frame the ray landed and WHICH face it landed on: the front,
    // or one of the four sides that are the board's edge.
    int   hitFrame = -1;
    int   hitFace = 0;           // 0 the front, 1 a left or right edge, 2 a top or bottom
    float hitSide = 1.0;         // which of the two, as the sign of the axis
    float tFrame = 1e9;
    vec2  framePoint = vec2(0.0);
    for (int i = 0; i < 10; i++)
    {
        if (i >= uFrames) break;
        vec3 ax = uFrameRight[i];
        vec3 ay = uFrameUp[i];
        vec3 rh = normalize(ax);
        vec3 uh = normalize(ay);
        vec3 nh = normalize(cross(ax, ay));

        // The ray in the frame's own coordinates: across it, up it, and out of its
        // face. The front of the board is w = 0 and its back is w = -thickness.
        vec3 ro = mouth - uFramePos[i];
        vec3 o = vec3(dot(ro, rh), dot(ro, uh), dot(ro, nh));
        vec3 dl = vec3(dot(rd, rh), dot(rd, uh), dot(rd, nh));
        // Never exactly nought: a ray running along a slab divides by it.
        dl = sign(dl + vec3(1e-12)) * max(abs(dl), vec3(1e-7));

        vec3 hi = vec3(length(ax) + uFrameBorder, length(ay) + uFrameBorder, 0.0);
        vec3 lo = vec3(-hi.x, -hi.y, -uFrameThick);

        vec3 ta = (lo - o) / dl;
        vec3 tb = (hi - o) / dl;
        vec3 tsmall = min(ta, tb);
        vec3 tbig = max(ta, tb);
        float tn = max(max(tsmall.x, tsmall.y), tsmall.z);
        float tf = min(min(tbig.x, tbig.y), tbig.z);
        if (tf < max(tn, 0.0)) continue;        // missed the board
        if (tn <= 0.0 || tn >= tFrame) continue;

        tFrame = tn;
        hitFrame = i;

        // Which face was entered: the slab that opened last.
        if (tn == tsmall.z)      { hitFace = 0; hitSide = -sign(dl.z); }
        else if (tn == tsmall.x) { hitFace = 1; hitSide = -sign(dl.x); }
        else                     { hitFace = 2; hitSide = -sign(dl.y); }

        vec3 q = o + dl * tn;
        framePoint = vec2(q.x / length(ax), q.y / length(ay));
    }

    // A fixed step lands on the same planes for every pixel and shows as rings across
    // the picture, so each ray starts a fraction of a step in.
    float jitter = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
    float far = length(vec3(uWidth, uHeight, uDepth)) * uReach;
    // The march stops at the frame: a picture is opaque, and light gathered past it
    // would be light shining through it.
    float marchTo = hitFrame >= 0 ? min(far, tFrame) : far;
    float dt = marchTo / float(max(uSteps, 1));

    // The box mapped into the field's own coordinates, with its centre at the origin.
    // uAniso says whether that mapping keeps the families' proportions or stretches
    // them to the box's.
    vec3 unit = mix(vec3(hh), vec3(hw, hh, hd), uAniso) / max(uFieldScale, 1e-3);
    // The box's own half-extents, in those coordinates: where a figure has to be put
    // to sit in the corner of the room.
    vec3 home = vec3(hw, hh, hd) / unit;

    vec3 lit = vec3(0.0);
    float trans = 1.0;
    // ...and what was left of it at the wall, which is the only part of the march that
    // is in front of the face. Taken at the crossing rather than at the end, or a
    // wall would be dimmed by light standing behind it.
    float wallTrans = 1.0;
    for (int i = 0; i < 160; i++)
    {
        if (i >= uSteps) break;
        float s = (float(i) + jitter) * dt;
        vec3 q = mouth + rd * s;
        vec4 v = roomField((q - vec3(0.0, 0.0, -hd)) / unit, home, uTime, uBass, uMid, uAir);

        // Out against the walls. The largest of the two across-the-room coordinates,
        // so the falloff is the shape of the room rather than a circle in a
        // rectangle, and a point beyond a wall stays at full strength: the light is
        // meant to carry on past them, only not to fill the middle.
        //
        // Not `out`: that is a parameter qualifier in GLSL and cannot name a value.
        float toWall = max(abs(q.x / hw), abs(q.y / hh));
        float wallness = smoothstep(uHollowEdge, 1.0, toWall);
        // ...and how far into the room this sample is. The two are taken as whichever
        // is the greater rather than multiplied: a sample earns its light by being
        // near a wall OR by being a long way off, and either alone is enough.
        float deepness = smoothstep(uBackFrom, 1.0, clamp(-q.z / max(uDepth, 1e-4), 0.0, 1.0));
        float keep = max(wallness, deepness * uCentreDim);
        float dens = v.w * uDensity * dt * mix(1.0, keep, uHollow);
        lit += v.rgb * dens * trans;
        trans *= exp(-dens * uAbsorb);
        if (s < tHit) wallTrans = trans;
    }

    // The light itself, and behind it either a picture or what is left of a face.
    vec3 col = lit;
    if (hitFrame >= 0)
    {
        vec3 ax = uFrameRight[hitFrame];
        vec3 ay = uFrameUp[hitFrame];
        vec3 rh = normalize(ax);
        vec3 uh = normalize(ay);
        vec3 nh = normalize(cross(ax, ay));

        if (hitFace == 0 && abs(framePoint.x) <= 1.0 && abs(framePoint.y) <= 1.0)
        {
            // The picture. Its v is flipped because a texture's first row is its top
            // and the room's y counts upward.
            vec2 art = framePoint * 0.5 + 0.5;
            col += texture(uArt, vec3(art.x, 1.0 - art.y, uFrameLayer[hitFrame])).rgb
                 * uFrameLight * trans;
        }
        else
        {
            // ── the board ──
            //
            // Flat. The face it was hit on decides the normal and nothing else does:
            // the front is one even tone across its whole width, and the sides are
            // each their own, so what gives the frame its shape is the ANGLE the board
            // is turned at rather than any profile cut into it.
            vec3 N = hitFace == 0 ? nh * hitSide
                   : hitFace == 1 ? rh * hitSide
                                  : uh * hitSide;

            // One light, from above and to the left. Fixed rather than taken from the
            // nebula: a board lit by a light that drifts has no shape.
            const vec3 L = normalize(vec3(-0.40, 0.72, 0.56));
            float lam = max(dot(N, L), 0.0);
            // Not `half`: reserved in GLSL, like `out` before it.
            vec3 toLight = normalize(L - rd);
            float sheen = hitFace == 0 ? pow(max(dot(N, toLight), 0.0), 42.0) * 0.28 : 0.0;

            // The sides are darker than the front whatever the light says. A board
            // seen edge-on is in its own shadow, and without this a side catching the
            // light reads as brighter than the face it belongs to.
            float sideDim = hitFace == 0 ? 1.0 : 0.62;

            col += (uFrameInk * (0.26 + 0.92 * lam) * sideDim + vec3(sheen)) * trans;
        }
    }
    else
    {
        col += wall * wallTrans * (1.0 + lit * uSpill);
    }

    col *= uExposure;
    // Held off the clip at the bright end, by as much as uTone asks for.
    col = mix(col, col / (1.0 + col), uTone);

    // Eight bits across gradients this soft bands visibly. A grain of dither, the
    // same figure PegasusStage ends on.
    float grain = fract(sin(dot(gl_FragCoord.xy, vec2(63.7264, 10.873))) * 24634.6345) - 0.5;
    col += grain * (uDither / 255.0);

    fragColor = vec4(col, 1.0);
}
