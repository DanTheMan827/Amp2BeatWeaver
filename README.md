# Amp2BeatWeaver

C# CLI tool to convert an Amplitude 2016 song folder into a BeatWeaver song folder.

## What it does

- Reads Amplitude song assets from an input directory (`.mid`, `.moggsong`, optional `.mogg`).
- Parses the `.moggsong` (DTA format via DtxCS) to extract track names, channel assignments, volumes and song metadata.
- Remaps Amplitude MIDI note numbers to their BeatWeaver equivalents (3 Amplitude lanes → 4 BeatWeaver lanes, mapped to inner-left / inner-right / outer-right per difficulty).
- Sets per-track MIDI `SequenceTrackName` (unique name) and `InstrumentName` (base instrument type) events from the moggsong data.
- Writes a BeatWeaver-compatible `.json` with the correct `metadata` and `audio` structure.
- Copies the `.mogg` file as `.ogg` for BeatWeaver.
- Produces a BeatWeaver song directory where all output files share the song name.

## Build

```bash
dotnet build
```

## Usage

```bash
dotnet run -- <amplitude-song-directory> <output-root-directory>
```

The output song name is derived from the `.moggsong` filename.

## Example

```bash
dotnet run -- /path/to/amplitude/slimenest /path/to/beatweaver/music
```

This creates:

```text
/path/to/beatweaver/music/slimenest/
  slimenest.mid
  slimenest.json
  slimenest.ogg    (copied from .mogg if present)
```

## Note mapping

| Difficulty | Amplitude Left | Amplitude Middle | Amplitude Right |
|------------|---------------|-----------------|----------------|
| Easy       | 96 → C#1 (25)  | 98 → D1 (26)    | 100 → D#1 (27) |
| Medium     | 102 → C#2 (37) | 104 → D2 (38)   | 106 → D#2 (39) |
| Hard       | 108 → C#3 (49) | 110 → D3 (50)   | 112 → D#3 (51) |
| Expert     | 114 → C#4 (61) | 116 → D4 (62)   | 118 → D#4 (63) |

BeatWeaver outer-left (positions 0: C1/C2/C3/C4) is left unused.
