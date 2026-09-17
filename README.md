### Media Segregator

Sorts phone photos and videos into a dated folder tree. The originals are **copied**, never moved,
so the source folder is left exactly as it was found.

```
2026_03_01/
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
`2026_07_04/Zdjęcia/52.5N 13.4E`. The threshold is 15 km, which is generous inside Poland and keeps
remote fixes under coordinates rather than misleading city names.

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

`MediaSegregator/Data/cities.tsv` holds 266 805 rows of `name<TAB>latitude<TAB>longitude`, sorted by
latitude, embedded in the executable. It is built from three [GeoNames](https://www.geonames.org/)
exports:

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
decimals for the Polish full list and kept as exported for the worldwide list.

Not every Polish alternate name is a name. An alternate is rejected, and the GeoNames primary name
kept instead, when it contains `://` or runs past 45 characters. Both rules are needed and both are
measured against the data: one row arrived as `https://en.wikipedia.org/wiki/Motarzyn` at 38
characters — *shorter* than the longest real name, `Jerzmanowo-Jarnołtów-Strachowice-Osiniec` at 39
— so length alone cannot catch it, while the 62-character holiday-let advertisement that had
displaced a `Nowa Wieś` has no URL in it to catch. Without both, two villages ship as folders named
after a Wikipedia link and a rental listing.

GeoNames data is licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

## License

Media Segregator is released under the [MIT License](LICENSE.txt).

The published `.exe` is self-contained: the .NET runtime, Avalonia (with Skia, HarfBuzz, ANGLE and
the Inter typeface), MetadataExtractor, XmpCore and the GeoNames place-name list are all inside it.
Their licences require that notice travel with the binary, so
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) is copied next to the `.exe` on publish — **ship
it, and `LICENSE.txt`, alongside the executable.**
