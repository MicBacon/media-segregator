### Media Segregator

To publish self-contained .exe file for windows use command
```
dotnet publish MediaSegregator/MediaSegregator.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true
```