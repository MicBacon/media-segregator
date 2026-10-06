### Media Segregator

Sorts phone photos and videos into a dated folder tree. The originals are **copied**, never moved,
so the source folder is left exactly as it was found.

```
2026-03-01/
  Zdjęcia/
    zrzut-ekranu.png            <- no coordinates, so no third level
    Warszawa/IMG_0001.jpg
    Zakopane/IMG_0002.jpg
  Wideo/
    Warszawa/VID_0003.mp4
Bez daty/
  Zdjęcia/skan.jpg              <- nothing in the file or its name says when
```

The date comes from EXIF or QuickTime metadata, falling back to the date camera apps bake into the
file name; the filesystem timestamp is deliberately never used. The place comes from the GPS fix in
the file's own metadata, resolved to the nearest settlement with no network call.

**Place names cover the world** — GeoNames' worldwide `cities500` list is embedded for photos taken
abroad, while Poland keeps the full town, village and hamlet list. A photo taken in the Bieszczady
is still filed under `Wetlina`, and a photo from Berlin now lands under `Berlin` instead of
`2026-07-04/Zdjęcia/52.5N 13.4E`. The threshold is 15 km: generous anywhere inhabited, and short
enough that open sea, desert and ice get coordinates instead of borrowing a name from over the
horizon. Nearest wins outright, with no weighting by size — the rule that files a Bieszczady photo
under its hamlet also means Tokyo Station lands under the ward nearest to it.

Running it twice over the same source is free: a file whose copy is already in the target folder
under the same name and the same length is skipped.

A scan also weighs the run against the destination drive and says so in the status line when it
will not fit — a warning rather than a refusal, because the figure covers the whole library while a
second run over an already-sorted one copies almost none of it.

To publish self-contained .exe file for windows use command
```
dotnet publish MediaSegregator/MediaSegregator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```

#### Place names

`MediaSegregator/Data/cities.tsv` holds 267 039 rows of `name<TAB>latitude<TAB>longitude`, sorted by
latitude, embedded in the executable — 221 613 from the world and 45 426 from Poland. It is built
from three [GeoNames](https://www.geonames.org/) exports:

| File | Used for |
| --- | --- |
| [`dump/cities500.zip`](https://download.geonames.org/export/dump/cities500.zip) | worldwide populated places with population over 500, or administrative seats down to PPLA4 |
| [`dump/PL.zip`](https://download.geonames.org/export/dump/PL.zip) | every feature in Poland |
| [`dump/alternatenames/PL.zip`](https://download.geonames.org/export/dump/alternatenames/PL.zip) | the Polish spelling of each name |

Rows are kept when the feature class is `P` (*city, village, …*), minus the codes that are not
places you can photograph today: `PPLX` (a district of a larger place, which would split Warszawa
into its boroughs), `PPLQ` and `PPLW` (abandoned), `PPLH` and `PPLCH` (historical). The worldwide
rows come from `cities500.zip`, excluding Polish rows so the more detailed Polish list can take
over. Polish names are replaced by their `isolanguage = pl` alternate name where one exists,
preferring the entry flagged `isPreferredName` and ignoring historical and colloquial spellings —
this is what turns GeoNames' "Warsaw" into "Warszawa". Latitude and longitude are rounded to four
decimals throughout.

##### Two vandalised rows

Not every GeoNames name is a name, and the junk is in the **primary** name column rather than in an
alternate. Two Polish villages ship as `https://en.wikipedia.org/wiki/Motarzyn` and as a
67-character holiday-let advertisement ending in the words `Nowa Wieś`; neither record has a Polish
alternate to fall back on, so no general rule can recover the real name. They are corrected by
GeoNames id — `12451017` → `Motarzyn`, `9036717` → `Nowa Wieś` — and
`NameFor_RowsWhoseAlternateNameWasNotAName` fails if a regeneration loses the correction.

Two sanity rules guard against the next one, and they are deliberately not the same rule
everywhere:

- **A name containing `://` is dropped, worldwide.** No real name in any of the three exports
  contains it, so this costs nothing and catches the Motarzyn class.
- **A Polish name longer than 45 characters is dropped.** The longest real Polish name is
  `Osiedle im. Józefa Montwiłła-Mireckiego` at 42, so there is headroom. This rule is **not**
  applied abroad: 65 world names are longer than 45 characters and every one is genuine, from
  Mexican and Italian compound names to the 97-character `United Townships of Dysart, Dudley,
  Harcourt, …` in Ontario.

A row whose name is junk, with no override and no usable alternate, is dropped rather than guessed
at — no folder name is better than a wrong one.

GeoNames data is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

Media Segregator is released under the [MIT License](LICENSE.txt).

The published `.exe` is self-contained: the .NET runtime, Avalonia (with Skia, HarfBuzz, ANGLE and
the Inter typeface), MetadataExtractor, XmpCore and the GeoNames place-name list are all inside it.
Their licences require that notice travel with the binary, so
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) is copied next to the `.exe` on publish — **ship
it, and `LICENSE.txt`, alongside the executable.**
