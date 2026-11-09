# Sample libraries for importing from other music apps

Made-up libraries, playlists and server answers. They are the input of the import readers' tests in
`rpm-desktop` (RPM.Core.Tests) and `rpm-web`. `expected/` holds what each reader must produce from
them. Every name, title, path and id is invented.

After a change, run `python3 scripts/sync_fixtures.py`, which copies this folder into both test
projects.

- `itunes/`, `musicbee/`: the iTunes library XML, as Apple Music exports it and as MusicBee writes it.
- `playlists/`: .m3u, .m3u8 and .pls files. The tests read them as if they were in
  `/Users/test/Playlists/`.
- `jellyfin/`, `plex/`, `subsonic/`: JSON answers of the servers' APIs. The Navidrome samples are read
  as the server `http://nas.local:4533` with the user `test`.
