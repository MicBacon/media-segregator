using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MediaSegregator;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private string _folderPath = FileScanner.DefaultFolder;
    private string _status = "Ready.";
    private bool _recurse;
    private CancellationTokenSource? _scanCts;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        Opened += (_, _) => _ = RescanAsync();
    }

    public ObservableCollection<ScannedFile> Files { get; } = [];

    public string FolderPath
    {
        get => _folderPath;
        set => Set(ref _folderPath, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool Recurse
    {
        get => _recurse;
        set
        {
            if (Set(ref _recurse, value))
            {
                _ = RescanAsync();
            }
        }
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose a folder to scan",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(FolderPath),
            });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            FolderPath = path;
            await RescanAsync();
        }
    }

    private void OnRescanClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = RescanAsync();

    private void OnFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = RescanAsync();
        }
    }

    private async Task RescanAsync()
    {
        // Supersede any scan still running against the previous folder.
        CancellationTokenSource cts = new();
        Interlocked.Exchange(ref _scanCts, cts)?.Cancel();
        CancellationToken token = cts.Token;

        string folder = FolderPath;
        bool recurse = Recurse;

        if (!Directory.Exists(folder))
        {
            Files.Clear();
            Status = $"Folder not found: {folder}";
            return;
        }

        Status = "Scanning…";
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            // Enumeration hits the disk, so keep it off the UI thread. Results are
            // materialized on the worker and handed over in one go, so the grid is
            // not re-laid-out once per file.
            List<ScannedFile> found = await Task.Run(
                () => FileScanner.Scan(folder, recurse, token).ToList(), token);

            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Files.Clear();
                foreach (ScannedFile file in found)
                {
                    Files.Add(file);
                }
            });

            long totalBytes = 0;
            foreach (ScannedFile file in found)
            {
                totalBytes += file.Length;
            }

            Status = $"{found.Count:N0} file(s) · {totalBytes / (1024.0 * 1024):N1} MB · {sw.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException)
        {
            // A newer scan took over; it owns the status text now.
        }
        catch (Exception ex)
        {
            Files.Clear();
            Status = $"Scan failed: {ex.Message}";
        }
    }

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
