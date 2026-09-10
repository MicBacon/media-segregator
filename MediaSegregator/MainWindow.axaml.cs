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
    private CancellationTokenSource? _scanCts;

    // The media found by the last scan; the UI only shows the counts, but the move
    // works off the exact set that was counted.
    private IReadOnlyList<ScannedFile> _files = [];

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

    /// <summary>True while a move is running, so the buttons stay out of the way.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
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

        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private void OnRescanClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RescanAsync();

    private void OnSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = RescanAsync();
        }
    }

    private async void OnMoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

        IReadOnlyList<ScannedFile> toMove = _files;

        if (toMove.Count == 0)
        {
            Status = "Brak plików do przeniesienia.";
            return;
        }

        IsBusy = true;
        Status = $"Przenoszenie {toMove.Count:N0} plik(ów)…";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            MoveResult result = await Task.Run(
                () => FileMover.Move(toMove, destination, DestinationLayout.TargetFor));

            string summary = $"Przeniesiono {result.Moved:N0} plik(ów) do {destination} · {sw.ElapsedMilliseconds} ms";

            if (result.Undated > 0)
            {
                summary += $" · bez daty: {result.Undated:N0}";
            }

            if (result.Skipped > 0)
            {
                summary += $" · pominięto {result.Skipped:N0} (już we właściwym folderze)";
            }

            if (result.Errors.Count > 0)
            {
                summary += $" · błędy: {result.Errors.Count:N0} ({result.Errors[0]})";
            }

            Status = summary;
        }
        catch (Exception ex)
        {
            Status = $"Przenoszenie nie powiodło się: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        SaveSettings();

        // The source folder just changed underneath us, so the counts are stale.
        await RescanAsync(keepStatus: true);
    }

    private async Task RescanAsync(bool keepStatus = false)
    {
        // Supersede any scan still running against the previous folder.
        CancellationTokenSource cts = new();
        Interlocked.Exchange(ref _scanCts, cts)?.Cancel();
        CancellationToken token = cts.Token;

        string folder = SourceFolder;

        if (!Directory.Exists(folder))
        {
            ShowResult(ScanResult.Empty);
            Status = $"Nie znaleziono folderu: {folder}";
            return;
        }

        if (!keepStatus)
        {
            Status = "Skanowanie…";
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // Enumeration hits the disk, so keep it off the UI thread.
            ScanResult result = await Task.Run(() => FileScanner.Scan(folder, token), token);

            token.ThrowIfCancellationRequested();
            ShowResult(result);

            if (!keepStatus)
            {
                Status = $"{result.Files.Count:N0} plik(ów) · "
                    + $"{result.TotalBytes / (1024.0 * 1024):N1} MB · {sw.ElapsedMilliseconds} ms";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer scan took over; it owns the status text now.
        }
        catch (Exception ex)
        {
            ShowResult(ScanResult.Empty);
            Status = $"Skanowanie nie powiodło się: {ex.Message}";
        }
    }

    private void ShowResult(ScanResult result)
    {
        _files = result.Files;
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
