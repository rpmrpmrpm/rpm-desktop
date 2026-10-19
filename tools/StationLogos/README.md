# StationLogos

Works out the best logo it can for every station in the Radio Browser directory, so the API can
serve one answer to everybody instead of every RPM install working it out again.

## Why

The app resolves station logos itself. Where the directory has no picture (22,698 of the 59,654
stations that answer, 38%), it reads the station's own website for the icon tags and the web app
manifest. Those 22,698 stations sit on **11,217 distinct sites**, and so every install would fetch
the same ones. On one machine, the pages that walk had cached came to 431
rows and 74.4MB, a third of a full 256MB cache.

Done here, the work happens once for everybody, the first visit is as fast as the second, and eleven
thousand broadcasters get asked once a month rather than once per user per visit.

## What it does

Three passes, each resumable.

1. **Measure** every logo the directory already names. 2KB off the front of each, which settles
   PNG, GIF, WebP, ICO and most JPEGs; only a JPEG hiding its SOF marker behind a colour profile
   costs a second request for the 64KB the app itself probes with.
2. **Walk** the site of every station that has nothing usable. This is per host, never per station, since
   a broadcaster's forty stations share one site and one answer.
3. **Measure** whatever the walk turned up, the same cheap way.

Then it tallies, and writes the ones it could find nothing for.

It reuses `RPM.Core.Radio.StationIcons` and `RPM.Core.Art.ImageHeader` rather than parsing its own.
A crawler with its own parser finds different candidates than the client does, and that difference
only shows up later as "the app disagrees with the server about this station".

**Choosing between them is this tool's own rule and not the app's `WorthOf`**, because what the
walk records is an address and a size, not the pixels. It prefers a squarish picture and then the
biggest. Ranked on size alone, it would serve 12,158 stations their
Open Graph social banner, because an og:image is 1200x630 and beats any real mark on pixels.

## Running it

The station dump first, one request rather than sixty thousand:

    curl -H 'User-Agent: RPM/1.0 (+https://rpmrpmrpm.com)' \
      'https://de1.api.radio-browser.info/json/stations/search?hidebroken=false&order=clickcount&reverse=true&limit=1000&offset=0'

Paged at 1,000; `scripts/` has the loop. Then:

    dotnet run -c Release -- --dump stations.json --out logo-crawl

| flag | default | |
| --- | --- | --- |
| `--dump` | `stations.json` | the directory dump |
| `--out` | `./logo-crawl` | where the notes go |
| `--at-once` | 256 | requests in flight across the whole crawl |
| `--timeout` | 10 | seconds for any one request |
| `--top` | all | only the N most clicked, for a trial run |
| `--phase` | `all` | `measure` or `walk` to run one pass |

## Manners

It asks eleven thousand third-party sites for something and none of them agreed to it.

- **One request per site at a time**, whatever `--at-once` is. Across 11,217
  hosts this limit almost never applies. Where it does (a page listing eight icons), firing the
  requests together earns a 429 and loses that station its logo entirely. So the limit raises the
  hit rate.
- **One attempt.** A site that did not answer is recorded as not having answered and asked again
  another day, not hammered now.
- **A short timeout**, so a dead host costs seconds.
- **It says who it is** and where to complain, in the user agent.

## The notes

`measured.jsonl` and `walked.jsonl`, one JSON object per line, appended as each answer lands and
read back on the next run so a stopped crawl resumes. Each line is keyed by the thing that was asked for, an
address or a host, because the work is done per address or per host.

`missing.json` is the output for the admin page: the stations nothing could be found for, with why,
most-clicked first.

## The tiers

The app's own numbers, not round ones. `RadioModel` draws a logo in a 51-point row well, which is
102 pixels at 2x (`RowLogoEdge`), and in the now playing square at 512 (`SquareLogoEdge`).

| | |
| --- | --- |
| 512 and over | good for the square, and for everything else |
| 102 to 511 | good for the row, soft on the square |
| under 102 | soft everywhere |
| nothing | no picture at any address it knows |
