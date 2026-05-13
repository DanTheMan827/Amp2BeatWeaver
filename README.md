# Amp2BeatWeaver

C# CLI tool to convert an Amplitude 2016 song input into a BeatWeaver song folder.

## Setup

This repo uses `AmpHelper` as a git submodule and consumes `DtxCS` from that submodule.

```bash
git submodule update --init --recursive
```

## Build

```bash
dotnet build
dotnet test
```

## Usage

```bash
dotnet run -- <amplitude-input> <output-root-directory>
```

`<amplitude-input>` may be any of:

- a song folder
- a `.moggsong` file inside a song folder
- a `.zip` file containing a song folder or a single song payload

## What it does

- Parses the Amplitude `.moggsong` with `DtxCS`
- Derives MIDI track instruments from moggsong track names
- Ensures BeatWeaver MIDI track names are unique
- Remaps the 3-lane Amplitude notes to BeatWeaver's 4-lane chart positions
- Adds a BeatWeaver `master` track with one-measure outro / transition / ending notes after the moggsong-reported song end
- Writes BeatWeaver `metadata` / `audio` JSON
- Copies `.mogg` to `.ogg`, stripping any leading non-Ogg header bytes first, padding with appended silence when a 48 kHz source is shorter than the converted MIDI, and re-encoding non-48 kHz sources to 48 kHz at 128 Kbps per channel

## Note mapping

Amplitude notes map to BeatWeaver's corresponding lanes shifted one slot to the right:

| Difficulty | Amplitude Left | Amplitude Middle | Amplitude Right |
|------------|----------------|------------------|-----------------|
| Easy       | 96 → 25        | 98 → 26          | 100 → 27        |
| Medium     | 102 → 37       | 104 → 38         | 106 → 39        |
| Hard       | 108 → 49       | 110 → 50         | 112 → 51        |
| Expert     | 114 → 61       | 116 → 62         | 118 → 63        |

BeatWeaver's outer-left lane remains unused for converted Amplitude notes.

## CI

GitHub Actions builds the app on pushes to any branch, pushes to tags, and manual dispatches. The workflow uploads the published artifact and runs the converter against all songs in `hmxmilohax/amp-2016-customs` with Git LFS enabled.
