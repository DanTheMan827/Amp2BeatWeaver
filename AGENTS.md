# AGENTS.md

## Keep this file updated

Update this file whenever the repository's build, test, validation, or converter verification process changes.

## Setup

- Initialize submodules before building or testing:

```bash
git submodule update --init --recursive
```

## Build and test

- Build the converter:

```bash
dotnet build /home/runner/work/Amp2BeatWeaver/Amp2BeatWeaver/Amp2BeatWeaver.csproj
```

- Run the repository tests:

```bash
dotnet test /home/runner/work/Amp2BeatWeaver/Amp2BeatWeaver/Amp2BeatWeaver.csproj
```

## Running the converter manually

- Convert from a song folder, `.moggsong`, or `.zip` input:

```bash
dotnet run --project /home/runner/work/Amp2BeatWeaver/Amp2BeatWeaver/Amp2BeatWeaver.csproj -- <amplitude-input> <output-root-directory>
```

- Audio verification details:
  - `.mogg` headers before the Ogg payload are stripped.
  - If the source audio is already 48 kHz, the converter keeps the original audio and appends silent Ogg data only when the MIDI runs longer.
  - If the source audio is not 48 kHz, the converter re-encodes it to 48 kHz and uses 128 Kbps per channel.

## Testing against the Amplitude customs repository

- CI validates the converter against `hmxmilohax/amp-2016-customs`.
- That repository uses Git LFS, so test checkouts should enable LFS.
- The workflow converts every song folder from the customs repository, then also tests a direct `.moggsong` input and a `.zip` input built from the first song directory.

Example local flow:

```bash
git clone https://github.com/hmxmilohax/amp-2016-customs.git
cd amp-2016-customs
git lfs pull
find songs -name '*.moggsong' | sort
dotnet run --project /home/runner/work/Amp2BeatWeaver/Amp2BeatWeaver/Amp2BeatWeaver.csproj -- /path/to/song-folder /tmp/output-folder
dotnet run --project /home/runner/work/Amp2BeatWeaver/Amp2BeatWeaver/Amp2BeatWeaver.csproj -- /path/to/song.moggsong /tmp/output-moggsong
```

To match CI more closely, also create a zip from a song folder and run the converter against that zip input.
