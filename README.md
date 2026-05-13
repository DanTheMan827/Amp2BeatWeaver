# Amp2BeatWeaver

C# CLI tool to convert an Amplitude 2016 song folder into a BeatWeaver song folder.

## What it does

- Reads Amplitude song assets from an input directory (`.mid`, `.moggsong`, optional `.mogg`).
- Rewrites MIDI notes for BeatWeaver compatibility (`--transpose` and optional explicit `--note-map`).
- Normalizes MIDI track and instrument names to BeatWeaver-safe names (lowercase, no punctuation, no spaces).
- Converts `.moggsong` metadata into a BeatWeaver `.json` song file.
- Produces a BeatWeaver song directory where files are named after the directory.

## Build

```bash
dotnet build
```

## Usage

```bash
dotnet run -- <amplitude-song-directory> <output-root-directory> [options]
```

Options:

- `--name <name>`: override output song name before normalization.
- `--instrument <name>`: MIDI instrument name (default: `guitar`).
- `--transpose <semitones>`: note transpose amount (default: `-12`).
- `--note-map <a:b,c:d>`: explicit note remap pairs (applied before transpose).

## Example

```bash
dotnet run -- /path/to/amplitude/song /path/to/output --instrument Guitar --transpose -12 --note-map "96:84,97:85,98:86,99:87,100:88"
```

This creates:

```text
/output/<normalized_song_name>/
  <normalized_song_name>.mid
  <normalized_song_name>.json
  <normalized_song_name>.mogg    (if source had a .mogg)
```
