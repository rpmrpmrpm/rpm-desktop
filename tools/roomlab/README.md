# room lab

A live sandbox for the playlist header's room. It is not part of the app and
nothing in the app reads it. The output is a look that the team agrees on, plus the settings
that produced it. The room is then prerendered rather than drawn per frame.

## Running it

    python3 tools/roomlab/serve.py

Then open http://localhost:8777. Edits to `room.frag` recompile the shader in
place while the page is open, and the panel keeps whatever it was tuned to. An
edit to `index.html` reloads the page. Shader errors print on the page and the
last shader that compiled stays on screen.

## What it draws

A box seen through its open front. The light inside it is marched, not stacked: the
ray is cut into `steps` samples between the mouth and the far wall, and at each one
the room's four families are asked what colour the light is there and how much of it
there is. That is what puts the light in the room's air rather than on its back wall.

The five faces are tints rather than surfaces. `walls` is how much of a face's own
colour survives; at a low setting the box reads as glass around the light, and the
marched light passes in front of the side walls instead of stopping at them.

The light is a nebula rather than a figure in the middle. Each of the three families
has `per family` figures, and every one of them has a place of its own in the volume
that it drifts about, rather than an orbit around the room's centre: orbits put all of
them in the middle and left the corners dark whatever the mapping did. `spread` is how
far those places reach, past one carrying them outside the walls so the light does not
end at them, and `size` is how much they overlap into a medium instead of reading as
separate blobs.

`stretch` is a separate question from filling the room, and keeping them separate is
the point. At nought a figure stays round; at one it is drawn out to the room's own
proportions, which in a header four times as wide as it is tall turns every figure
into a horizontal streak.

Nothing is laid over the top: no dim, no vignette, no veil.

The ray is aimed through the opening rather than through a field of view, so the
mouth of the room always fills the frame and the camera controls change only the
angle it is seen from.

## Where the light comes from

`room.frag` carries its own field. The families are `RoomSource`'s, with the same counts,
rates, phases, falloff and colour weighting, each given a third coordinate, because
`rpmRoom` is a function of a plane and no sampling of a plane is a volume. The cost of this
separate copy is that a change to the figures in Core does not reach the lab. The two can
drift apart without any warning, so change both.

## The walls

There are none by default: `walls` starts at nought and nothing is painted at the
faces. The march does not stop at them either, because clipping it there would draw the
box's silhouette in the light itself, a hard diagonal across the corners that reads as
a wall whether or not a wall is painted. `reach` is how far the ray runs instead, as a
multiple of the box's diagonal, and the light ends where the field ends.

The box still shapes the light, through where the figures are placed. The light has no edge
that belongs to the box.

Turning `walls` up puts the faces back as tints, with the light passing in front of
and through them.

## The frames

Each frame is an oriented rectangle: it has its own place in the room, its own size,
and its own angle. `turn` swings a frame toward the middle by more the further out it
stands, so it foreshortens the way the room does; at nought they all face straight out
and read as stickers laid on the picture rather than as things in it.

The march stops at the nearest frame a ray meets, so a frame is fogged by whatever
light stands in front of it and hides whatever stands behind: one at the back of the
room is buried in the nebula, one near the window is clean.

**A frame is a board, not a plane.** The picture's rectangle grown by `border` and
extruded backwards by `thickness`, so the intersection is a slab test in the frame's
own axes rather than a ray against a plane, and what comes out of it is both where the
ray landed and which face it landed on. The front is flat and one even tone across its
whole width; the four sides are each their own tone and are seen wherever the board is
turned away from the camera. That side is the whole of what makes it read as an object,
and it needs no bevel to do it.

The light is fixed, from above and to the left, and deliberately not taken from the
nebula: a board lit by a light that drifts has no shape. The sides are held below the
front whatever the light says, because a board seen edge-on is in its own shadow and a
side catching the light brighter than its own face reads as a mistake.

The moulding and the playlist tile's edge are one material: `TILE_EDGE` in index.html
paints the tile and `uFrameInk` the moulding, from the same value, so the frames in the
room and the tile over them cannot drift apart.

**Where they hang is worked out on the page, not in the shader, because that is the
part the app needs.** The room is prerendered and the pictures are not, so what has to
come out of the lab is the rectangle each cover is mapped into. **Copy settings**
carries a `frames` array of `{x, y, z, halfW, halfH, yawDeg, cover}` in world units
alongside every control.

Across the picture the frames are stratified, one to a column, rather than taken from
the same sequence the other two axes use. Six low-discrepancy samples in a room four
times as wide as it is tall can come out in a clump at one end.

**A lane is an equal share of the FREE width, not of the width.** The cover tile takes
the left quarter from top to bottom and the writing takes a band under the middle, so
an even split would give the blocked columns as many frames as the clear ones, and the right
side, which has the most room in the header, would get one frame and look bare. Each
column is worth what is left of it after the chrome, and a column under the tile
is worth almost nothing, so its share goes to its neighbours.

No two pictures in a header are the same. The importer takes one photograph from every
artist before it takes a second from any, so a playlist of six artists still has twelve
different pictures; the frames are only ever given layers that hold different ones; and
the cover tile quilts four the frames are not using.

The pictures are the landing page's ten covers, served from `site/covers` rather than
copied. Missing covers are not fatal; the page draws its own tile instead.

## The real playlists

    python3 tools/roomlab/import_library.py --list   # what is there
    python3 tools/roomlab/import_library.py          # -> library.json + artists/

Two databases, both COPIED before anything reads them: they are live files with write
ahead logs beside them and the app may be running. The peer mirror under
`Application Support/RPM/peers/<device>` supplies the playlists, and this machine's own
library supplies the artist pictures, because the peer's copy has art for far fewer artists
than the full library.

**The pictures are the ones the app has already fetched.** At runtime the app asks
fanart.tv on demand, keyed by the artist's MusicBrainz id and the user's own API key.
A lab should not use that key, so what is used here is what that pipeline has
already cached in `artist_art`: the same pictures, ranked by the same ranker. An
artist it has never fetched has no picture and is skipped, so only the playlists whose
artists all have pictures qualify.

**The colour is the app's own function.** `RoomPalette.From` decides the four colours
a room is painted in, and `import_library.py` transcribes it rather than approximating
it, along with `CoverColor`'s dominant and highlight buckets and its 48-square decode.
What is NOT the app's is where the three colours going in come from: the app takes
them off the records on the list, and these come off the artists' pictures, because
the peer's cover files are keyed by a hash the lab cannot reproduce. That source can be swapped
if the mapping is ever reproduced.

The picker in the toolbar chooses a playlist; its artists go in the frames and its
palette paints the room. A shot can name one:

    tools/roomlab/shot.sh a.png "playlist=Rock%20Anthems&w=1600&h=400"

With no playlist named, one is picked at random, as are the moment the light is caught
at and where the frames hang. `library.json`, `artists/` and `cache/` are not
committed.

## The chrome the room sits under

The room is not the whole header. A back arrow, the playlist's own cover tile, the
name with its two lines, and the three transport buttons are drawn on top of it, and a
frame behind the cover tile is simply never seen while one behind the writing competes
with it. `CHROME` in index.html holds those four rectangles as fractions of the header
from its top-left, measured off a screenshot of the running app at 2136 x 488, and the
layout treats them as occupied: `off chrome` is how much of a frame may fall under
them before the placement is rejected.

**They are written down, not discovered.** Nothing in the lab can learn where the
app's chrome is, so when the header's layout changes these numbers are wrong until
someone changes them.

The chrome is also DRAWN, not just outlined: the back arrow, the cover tile, the name,
the Featuring line, the song count and length, and the three transport buttons, on a
2D canvas over the GL one and composited into anything saved. A room is only judged
against what actually covers it. **Guides** still draws the four rectangles as dashed
outlines, and **Chrome** turns the drawing off to see the room bare.

Two things about it are stand-ins rather than copies, and both are visible: the type
is the system's rather than the app's faces, and the cover tile quilts four of the
playlist's ARTIST pictures because the lab does not have the records' covers. The
sizes, weights and positions are the app's.

That constraint is also why a blocked frame may wander out of its column. Across the
room the frames are stratified one to a column, and the cover tile is most of the
height of the column it stands in: a frame fixed to that column would have nowhere to go and
would end up behind the tile. The jitter within a column widens as the attempts
run out, so a frame with no room migrates instead.

The output size in the toolbar starts at 2136 x 488, which is the header's own size.

## Pictures bigger than the viewport

`window` is how much of the opening the picture shows. Below one the frame becomes a
window onto a bigger room: the light and the pictures carry on past the edges, and a
cover near the camera can be taller than the whole header.

**The box's own dimensions cannot do this**, which is worth saying because it is not
obvious. The rays are aimed through the opening, so making the box bigger scales the
room and the view of it together and changes nothing at all.

## The cost of a frame

The toolbar prints milliseconds a frame and milliseconds per megapixel, measured
after `gl.finish()` so it is the shader being timed rather than the queue.

At the defaults a pixel costs 112 steps times 48 figures, which is about 5,400 lobe
evaluations, against 15 for the flat Pegasus room. `steps` is what buys the difference
between a clean picture and a grainy one, and it is the first thing to drop if this
has to run live rather than be prerendered: at 48 the march noise is visible in
the dark parts, at 112 it is gone.

## Looking at it from outside the browser

`firefox --headless --screenshot` cannot be used for this, for two separate reasons.
It composites a WebGL canvas as black whatever the shader drew, and it exits at the
load event, which is before the cover textures have arrived.

`shot.sh` runs Firefox as an ordinary headless browser on a throwaway profile, waits
for the page to post its own canvas back to the server, and then closes it:

    tools/roomlab/shot.sh a.png "t=8&w=1600&h=400"

It prints the page's status line and the shader's compile log alongside the picture. A
blank shot therefore always comes with a reason.

The PNG lands in `shots/`. The query may carry any control by name, so a sweep of one
knob is a handful of these:

    tools/roomlab/shot.sh s1.png "uFrames=9&uFrameSize=0.14"

and `sheet.py` stacks the results into one picture, which is how a sweep is compared
rather than opened one at a time:

    python3 tools/roomlab/sheet.py tools/roomlab/shots s1.png s2.png s3.png

`shots/` is not committed.

## Output

**Save PNG** renders at the output size in the toolbar, which is the header's
banner proportion. **Copy settings** puts every control plus the time on the
clipboard as JSON, which is what a prerender is specified by; **Paste settings**
takes it back. Tuning is also kept in localStorage, so a reload does not lose it.
