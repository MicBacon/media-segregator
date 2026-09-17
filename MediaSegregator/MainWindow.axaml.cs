using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace MediaSegregator;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private string _sourceFolder;
    private string _destinationFolder;
    private string _status = "Gotowy.";
    private int _photoCount;
    private int _videoCount;
    private bool _isBusy;
    private double _progress;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _copyCts;

    // The media found by the last scan; the UI only shows the counts, but the copy
    // works off the exact set that was counted.
    private IReadOnlyList<ScannedFile> _files = [];

    // What the scan weighed, which is what turns bytes copied into a percentage.
    private long _totalBytes;

    // The folder _files was read from. The textbox writes through on every keystroke, so the path
    // shown can have moved on since the last scan, and copying the previous folder's list would
    // quietly sort the wrong library.
    private string? _scannedFolder;

    // The last progress the running copy reported, so a cancelled run can still say how far it got.
    private CopyProgress? _lastProgress;

    // While the destination still mirrors the source, picking a new source folder
    // carries the destination along; the first deliberate change to the destination
    // unlinks the two for the rest of the session.
    private bool _destinationFollowsSource = true;
    private bool _mirroringDestination;

    public MainWindow()
    {
        _settings = AppSettings.Load();

        // Last-used folder, falling back to the executable's own directory the
        // first time the app runs (or if the remembered folder has since gone).
        _sourceFolder = Existing(_settings.SourceFolder) ?? FileScanner.DefaultFolder;
        _destinationFolder = Existing(_settings.DestinationFolder) ?? _sourceFolder;
        _destinationFollowsSource = string.Equals(_destinationFolder, _sourceFolder, StringComparison.Ordinal);

        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        Opened += (_, _) => _ = RescanAsync();
        Closing += (_, _) => SaveSettings();
    }

    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (Set(ref _sourceFolder, value) && _destinationFollowsSource)
            {
                _mirroringDestination = true;
                DestinationFolder = value;
                _mirroringDestination = false;
            }
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (Set(ref _destinationFolder, value) && !_mirroringDestination)
            {
                _destinationFollowsSource = false;
            }
        }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public int PhotoCount
    {
        get => _photoCount;
        private set => Set(ref _photoCount, value);
    }

    public int VideoCount
    {
        get => _videoCount;
        private set => Set(ref _videoCount, value);
    }

    /// <summary>True while a copy is running, so the buttons stay out of the way.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    /// <summary>How much of the run is done, as a percentage of the bytes the scan weighed.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    private async void OnBrowseSourceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickFolderAsync("Wybierz folder z mediami", SourceFolder) is { } path)
        {
            SourceFolder = path;
            await RescanAsync();
        }
    }

    private async void OnBrowseDestinationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await PickFolderAsync("Wybierz folder docelowy", DestinationFolder) is { } path)
        {
            DestinationFolder = path;
        }
    }

    private async Task<string?> PickFolderAsync(string title, string startAt)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startAt),
            });

        if (picked.Count == 0)
        {
            return null;
        }

        if (picked[0].TryGetLocalPath() is { } path)
        {
            return path;
        }

        // On Windows a phone appears under "Ten komputer" as an MTP device rather than a drive,
        // and a OneDrive folder can be online-only; neither has a path the file APIs can open.
        // Saying so beats the picker closing as though nothing had been chosen at all.
        Status = "Ten folder nie jest zwykłym folderem na dysku — telefon podłączony przez MTP "
            + "nie ma litery dysku. Podłącz kartę pamięci albo skopiuj pliki na dysk.";

        return null;
    }

    private void OnRescanClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RescanAsync();

    private async void OnLicensesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Window licenses = new()
        {
            Title = "Licencje — Segregator mediów",
            Width = 680,
            Height = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Margin = new Avalonia.Thickness(20),
                Content = new TextBlock
                {
                    Text = LicenseText,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };

        await licenses.ShowDialog(this);
    }

    private const string LicenseText = """
        Segregator mediów

        Ten program jest udostępniany na licencji MIT.
        Copyright (c) 2026 Michał Boczoń

        Licencja MIT pozwala używać, kopiować, modyfikować i rozpowszechniać program, pod warunkiem
        zachowania informacji o prawach autorskich i treści licencji. Program jest udostępniany
        „tak jak jest”, bez gwarancji.

        Program zawiera także dane GeoNames (lista nazw miejsc w Data/cities.tsv), udostępniane
        na licencji CC BY 4.0. Autorstwo: GeoNames. Szczegóły: https://creativecommons.org/licenses/by/4.0/

        Program korzysta również z bibliotek .NET, Avalonia, MetadataExtractor i ich zależności.
        Pełna lista komponentów, autorów i treści wymaganych licencji znajduje się w pliku
        THIRD-PARTY-NOTICES.txt, dostarczanym obok pliku wykonywalnego.

        Pełny tekst licencji programu znajduje się w pliku LICENSE.txt, również dostarczanym obok
        pliku wykonywalnego.
        """;

    private void OnSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = RescanAsync();
        }
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        string source = SourceFolder;
        string destination = DestinationFolder;

        if (!Directory.Exists(source))
        {
            Status = $"Folder z mediami nie istnieje: {source}";
            return;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            Status = "Wskaż folder docelowy.";
            return;
        }

        // Claimed before the first await, not after: everything below yields to the UI thread at
        // least once, and Kopiuj is kept out of a second, overlapping run only by this flag
        // already being up when the click comes back round.
        IsBusy = true;

        try
        {
            if (!string.Equals(_scannedFolder, source, StringComparison.Ordinal))
            {
                await RescanAsync();
            }

            IReadOnlyList<ScannedFile> toCopy = _files;
            long totalBytes = _totalBytes;

            if (toCopy.Count == 0)
            {
                Status = "Brak plików do skopiowania.";
                return;
            }

            // Checked again here rather than trusted from the scan: the destination can have been
            // retyped since, and this is the last moment before a copy that may run for an hour.
            string warning = await Task.Run(() => SpaceWarning(destination, totalBytes));

            using CancellationTokenSource cts = new();
            _copyCts = cts;
            _lastProgress = null;

            Progress = 0;
            Status = $"Kopiowanie {toCopy.Count:N0} plik(ów) · {Size(totalBytes)}…{warning}";
            Stopwatch sw = Stopwatch.StartNew();

            // Built here, on the UI thread, so it marshals every report back to it by itself.
            IProgress<CopyProgress> reporter = new Progress<CopyProgress>(progress =>
            {
                _lastProgress = progress;

                // The bar tracks everything the run has dealt with, not just what it wrote: a
                // second run over a sorted library copies nothing and would otherwise sit at
                // nought throughout. What was actually copied is in the summary the run ends with.
                Progress = totalBytes > 0 ? Math.Min(100, progress.BytesSettled * 100.0 / totalBytes) : 0;
                Status = $"Kopiowanie {Size(progress.BytesSettled)} z {Size(totalBytes)} · {progress.CurrentFile}";
            });

            await RunCopyAsync(toCopy, destination, cts, reporter, sw);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _copyCts = null;
        }

        SaveSettings();

        // No rescan: copying leaves the source folder exactly as the scan found it.
    }

    /// <summary>
    /// The copy itself, and the status line it leaves behind. Split out so <see cref="IsBusy"/> can
    /// be claimed before the first await above and released in one place once everything is done.
    /// </summary>
    private async Task RunCopyAsync(
        IReadOnlyList<ScannedFile> toCopy,
        string destination,
        CancellationTokenSource cts,
        IProgress<CopyProgress> reporter,
        Stopwatch sw)
    {
        try
        {
            CopyResult result = await Task.Run(
                () => FileCopier.Copy(toCopy, destination, DestinationLayout.TargetFor, cts.Token, reporter));

            string summary = $"Skopiowano {result.Copied:N0} plik(ów) · {Size(result.BytesCopied)} "
                + $"do {destination} · {sw.ElapsedMilliseconds:N0} ms";

            if (result.Undated > 0)
            {
                summary += $" · bez daty: {result.Undated:N0}";
            }

            if (result.Skipped > 0)
            {
                summary += $" · pominięto {result.Skipped:N0} (już skopiowane)";
            }

            if (result.Errors.Count > 0)
            {
                summary += $" · błędy: {result.Errors.Count:N0} ({result.Errors[0]})";
            }

            Status = summary;
        }
        catch (OperationCanceledException)
        {
            // The exception carries no tally, so the last report the run posted is what is left.
            Status = $"Przerwano · skopiowano {_lastProgress?.FilesDone ?? 0:N0} plik(ów) "
                + $"({Size(_lastProgress?.BytesDone ?? 0)})";
        }
        catch (Exception ex)
        {
            Status = $"Kopiowanie nie powiodło się: {ex.Message}";
        }
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_copyCts is { } cts)
        {
            Status = "Przerywanie…";
            cts.Cancel();
        }
    }

    /// <summary>
    /// The clause to hang off a status line when the run will not fit where it is going, and an
    /// empty string when it will or when the system would not say.
    ///
    /// A warning rather than a refusal, deliberately. The figure is the whole library, while a
    /// second run over an already-sorted one copies almost none of it — blocking on this number
    /// would lock the user out of exactly the cheap, repeatable run the skip rule was built for.
    /// </summary>
    private static string SpaceWarning(string destination, long bytes)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return string.Empty;
        }

        SpaceCheck space = FileCopier.RoomFor(destination, bytes);

        return space is { Fits: false, Available: { } free }
            ? $" · uwaga: w folderze docelowym wolne tylko {Size(free)} z potrzebnych {Size(bytes)}"
            : string.Empty;
    }

    /// <summary>Bytes as the largest unit that keeps the number readable.</summary>
    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        >= 1024 => $"{bytes / 1024.0:N1} kB",
        _ => $"{bytes:N0} B",
    };

    private async Task RescanAsync()
    {
        // Supersede any scan still running against the previous folder.
        CancellationTokenSource cts = new();
        Interlocked.Exchange(ref _scanCts, cts)?.Cancel();
        CancellationToken token = cts.Token;

        string folder = SourceFolder;

        if (!Directory.Exists(folder))
        {
            ShowResult(folder, ScanResult.Empty);
            Status = $"Nie znaleziono folderu: {folder}";
            return;
        }

        Status = "Skanowanie…";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // Enumeration hits the disk, so keep it off the UI thread.
            ScanResult result = await Task.Run(() => FileScanner.Scan(folder, token), token);

            token.ThrowIfCancellationRequested();
            ShowResult(folder, result);

            // Now that the run has a weight, say up front whether it can land — a scan is the only
            // moment the whole figure is known before anything has been copied. DriveInfo is a
            // disk call like the scan itself, so it stays off the UI thread too.
            string destination = DestinationFolder;
            string warning = await Task.Run(() => SpaceWarning(destination, result.TotalBytes), token);

            Status = $"{result.Files.Count:N0} plik(ów) · {Size(result.TotalBytes)} "
                + $"· {sw.ElapsedMilliseconds:N0} ms{warning}";
        }
        catch (OperationCanceledException)
        {
            // A newer scan took over; it owns the status text now.
        }
        catch (Exception ex)
        {
            ShowResult(folder, ScanResult.Empty);
            Status = $"Skanowanie nie powiodło się: {ex.Message}";
        }
    }

    private void ShowResult(string folder, ScanResult result)
    {
        _scannedFolder = folder;
        _files = result.Files;
        _totalBytes = result.TotalBytes;
        PhotoCount = result.Photos;
        VideoCount = result.Videos;
    }

    private void SaveSettings()
    {
        _settings.SourceFolder = SourceFolder;
        _settings.DestinationFolder = DestinationFolder;
        _settings.Save();
    }

    private static string? Existing(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;

    public new event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
