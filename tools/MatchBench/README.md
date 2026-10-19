# MatchBench

What one own-DB fingerprint lookup costs at a given store size, and whether it still gives the
right answer. It reproduces a failure of the recognition service:
the service returned `Runtime.OutOfMemory` rather than timing out, and neither the size of the
vote tally nor the peak heap beside a lookup is visible from outside the function.

```
dotnet run -c Release --project tools/MatchBench -- [songs] [queries] [stride] [schemeId]
```

| argument | default | what it is |
| --- | --- | --- |
| `songs` | 13332 | store size to build up to, the shape `GET /health` reported |
| `queries` | 200 | how many real recordings to ask about |
| `maxQuerySamples` | 500 | `ChromaMatcher.MaxQuerySamples` |
| `schemeId` | the default scheme | 1 = `ByteWindows`, 2 = `HalfWords` |

`RPM_LIBRARY` points at a desktop `library.db` (`<music folder>/.rpm/library.db`) whose stored
fingerprints become the real part of the corpus. It is required.

## The corpus is deliberately two things

Real recordings out of a library, padded to the target size with synthetic ones.

- **Recall and precision are scored on the real ones only.** Synthetic frames are uniform random
  and real chroma bytes are not, so a matcher scored against synthetic distractors flatters itself.
- **Cost is measured against the whole padded set,** because what a lookup costs depends on how
  many entries sit in the buckets it walks and not on what produced them.

## Reading the score

`stage 1 found the right audio` is the number a key scheme moves: whether stage 1 put the right
recording in the candidate set and stage 2 ranked it best. The line under it is that number after
the confidence gates, which a key scheme does not touch.

The two differ by a lot on a real library, and the reason is not a defect. A library holds the same
recording more than once, for example the same track on an album and on a compilation, or a FLAC and
an MP3 of one file. Both copies score identically, so `MarginToRunnerUp` is zero and neither is confidently
identified. Correctness is therefore scored by alignment against the answer's own fingerprint, not
by row id: answering with the other copy of the same audio is not a mistake.

## What it measured

13,332 songs, 844 of them real, 500 query samples:

| | `ByteWindows` (1) | `HalfWords` (2) |
| --- | --- | --- |
| index buckets | 1,024 | 327,678 |
| entries per bucket | 3,124 | 12 |
| stage 1 found the right audio | 100/100 | 100/100 |
| clip found its source | 100/100 | 100/100 |
| confidently wrong | 0 | 0 |
| median lookup | 472 ms | 10 ms |
| allocated per lookup, worst | 811 MB | 5 MB |
| held by the function | ~100 MB blobs + ~200 MB index | 32 MB, all index |

The function has 1,769 MB for everything. Running out of that is what the `Runtime.OutOfMemory` failures were.

Larger runs say where the next ceiling is. At 50,000 songs the function would hold 119 MB of index
and up to 21 MB in flight, and a lookup is 11 ms; at 200,000 it is 474 MB and 55 MB, and 37 ms.
Recall and precision are unchanged at both.

Note that `peak managed heap` counts the corpus, which the bench holds and the function does not.
It stands in for the DynamoDB table that stage 2 fetches from. `the function would hold` is the line to
read for the deployed shape.

## Pitfalls it shows

- **Half-words with a fixed query stride of 4 give clip recall of 79/100.** A twenty-second clip
  contains about five of a recording's sixty anchors, and a fixed stride throws away three quarters of
  the few chances there are. Bounding the number of samples (`maxQuerySamples`) instead of fixing the
  stride gives 100/100, and also lifts `ByteWindows` from 98 to 100.
- **A stop-word threshold of "a tenth of the collection" empties the index outright** under
  `ByteWindows`, whose average bucket is already a third of that. Ten times the mean bucket says the
  same thing at any store size and cannot do this.
