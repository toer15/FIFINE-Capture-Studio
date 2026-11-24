using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using FifineCaptureStudio.Library;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private bool _libraryScanning;
    private string _librarySearch = string.Empty;
    private string _libraryPlatform = "All platforms";
    private string _libraryResolution = "All resolutions";
    private string _librarySort = "Newest first";
    private RecordingItem? _selectedRecording;
    private string _newRecordingName = string.Empty;

    public ObservableCollection<RecordingItem> Recordings { get; } = new();
    public ObservableCollection<string> LibraryPlatforms { get; } = new() { "All platforms" };
    public ObservableCollection<string> LibraryResolutions { get; } = new() { "All resolutions" };
    public ObservableCollection<string> LibrarySorts { get; } = new()
    {
        "Newest first", "Oldest first", "Largest first", "Longest first", "Name A–Z"
    };

    public ICollectionView? RecordingView { get; private set; }

    public ICommand PlayRecordingCommand { get; private set; } = null!;
    public ICommand RevealRecordingCommand { get; private set; } = null!;
    public ICommand DeleteRecordingCommand { get; private set; } = null!;
    public ICommand RenameRecordingCommand { get; private set; } = null!;
    public ICommand CopyRecordingPathCommand { get; private set; } = null!;
    public ICommand RefreshLibraryCommand { get; private set; } = null!;

    public bool LibraryScanning
    {
        get => _libraryScanning;
        private set => Set(ref _libraryScanning, value);
    }

    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (Set(ref _librarySearch, value))
            {
                RecordingView?.Refresh();
                Raise(nameof(LibrarySummary));
            }
        }
    }

    public string LibraryPlatform
    {
        get => _libraryPlatform;
        set
        {
            if (Set(ref _libraryPlatform, value))
            {
                RecordingView?.Refresh();
                Raise(nameof(LibrarySummary));
            }
        }
    }

    public string LibraryResolution
    {
        get => _libraryResolution;
        set
        {
            if (Set(ref _libraryResolution, value))
            {
                RecordingView?.Refresh();
                Raise(nameof(LibrarySummary));
            }
        }
    }

    public string LibrarySort
    {
        get => _librarySort;
        set
        {
            if (Set(ref _librarySort, value))
            {
                ApplySort();
            }
        }
    }

    public RecordingItem? SelectedRecording
    {
        get => _selectedRecording;
        set
        {
            if (Set(ref _selectedRecording, value))
            {
                NewRecordingName = value?.DisplayName ?? string.Empty;
                Raise(nameof(HasSelectedRecording));
            }
        }
    }

    public bool HasSelectedRecording => _selectedRecording is not null;

    public string NewRecordingName
    {
        get => _newRecordingName;
        set => Set(ref _newRecordingName, value);
    }

    public string LibrarySummary
    {
        get
        {
            var all = Recordings.Count;
            var shown = RecordingView?.Cast<object>().Count() ?? all;
            var bytes = Recordings.Sum(r => r.SizeBytes);
            if (all == 0)
            {
                return "No recordings yet";
            }

            var shownLabel = shown == all ? $"{all} recording{(all == 1 ? "" : "s")}" : $"{shown} of {all} recordings";
            return $"{shownLabel} · {FormatBytes(bytes)} on disk";
        }
    }

    public bool LibraryEmpty => Recordings.Count == 0 && !LibraryScanning;
    public string LibraryFolder => Config.Recording.Directory;

    private void InitializeLibrary()
    {
        RecordingView = CollectionViewSource.GetDefaultView(Recordings);
        RecordingView.Filter = FilterRecording;

        PlayRecordingCommand = new RelayCommand(p =>
        {
            if (AsItem(p) is { } item)
            {
                _services.Library.Play(item);
            }
        });

        RevealRecordingCommand = new RelayCommand(p =>
        {
            if (AsItem(p) is { } item)
            {
                _services.Library.Reveal(item);
            }
        });

        DeleteRecordingCommand = new RelayCommand(p =>
        {
            if (AsItem(p) is { } item)
            {
                if (_services.Library.Delete(item))
                {
                    Recordings.Remove(item);
                    RefreshViewMetadata();
                    Notify("Deleted " + item.FileName, false);
                }
                else
                {
                    Notify("Could not delete " + item.FileName + " — it may be in use.", true);
                }
            }
        });

        RenameRecordingCommand = new RelayCommand(p =>
        {
            var item = AsItem(p) ?? SelectedRecording;
            if (item is null || string.IsNullOrWhiteSpace(NewRecordingName))
            {
                return;
            }

            var renamed = _services.Library.Rename(item, NewRecordingName);
            if (renamed is null)
            {
                Notify("Rename failed.", true);
                return;
            }

            _ = RefreshLibraryAsync();
            Notify("Renamed to " + Path.GetFileName(renamed), false);
        });

        CopyRecordingPathCommand = new RelayCommand(p =>
        {
            if (AsItem(p) is { } item)
            {
                try
                {
                    System.Windows.Clipboard.SetText(item.Path);
                    Notify("Path copied to clipboard.", false);
                }
                catch (Exception ex)
                {
                    Notify("Clipboard unavailable: " + ex.Message, true);
                }
            }
        });

        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync);
    }

    private static RecordingItem? AsItem(object? parameter) => parameter as RecordingItem;

    public async Task RefreshLibraryAsync()
    {
        if (RecordingView is null)
        {
            return;
        }

        LibraryScanning = true;
        Raise(nameof(LibraryEmpty));
        try
        {
            var items = await _services.Library.ScanAsync(Config.Recording.Directory);

            var selectedPath = SelectedRecording?.Path;
            Recordings.Clear();
            foreach (var item in items)
            {
                Recordings.Add(item);
            }

            RebuildLibraryFilters();
            ApplySort();
            Raise(nameof(LibraryEmpty));
            Raise(nameof(LibrarySummary));
            Raise(nameof(LibraryFolder));

            if (selectedPath is not null)
            {
                SelectedRecording = Recordings.FirstOrDefault(r => r.Path == selectedPath);
            }

            foreach (var item in Recordings)
            {
                var target = item;
                var thumb = await _services.Library.LoadThumbnailAsync(target);
                if (thumb is not null)
                {
                    target.Thumbnail = thumb;
                }
            }
        }
        catch (Exception ex)
        {
            Notify("Library scan failed: " + ex.Message, true);
        }
        finally
        {
            LibraryScanning = false;
            Raise(nameof(LibraryEmpty));
            Raise(nameof(LibrarySummary));
        }
    }

    private void RebuildLibraryFilters()
    {
        var platforms = Recordings.Select(r => r.PlatformBadge).Distinct().OrderBy(p => p).ToList();
        LibraryPlatforms.Clear();
        LibraryPlatforms.Add("All platforms");
        foreach (var platform in platforms)
        {
            LibraryPlatforms.Add(platform);
        }

        if (!LibraryPlatforms.Contains(_libraryPlatform))
        {
            _libraryPlatform = "All platforms";
            Raise(nameof(LibraryPlatform));
        }

        var resolutions = Recordings
            .Where(r => r.Width > 0)
            .Select(r => r.ResolutionLabel)
            .Distinct()
            .OrderByDescending(r => r)
            .ToList();
        LibraryResolutions.Clear();
        LibraryResolutions.Add("All resolutions");
        foreach (var resolution in resolutions)
        {
            LibraryResolutions.Add(resolution);
        }

        if (!LibraryResolutions.Contains(_libraryResolution))
        {
            _libraryResolution = "All resolutions";
            Raise(nameof(LibraryResolution));
        }
    }

    private bool FilterRecording(object obj)
    {
        if (obj is not RecordingItem item)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_librarySearch))
        {
            var q = _librarySearch.Trim();
            if (!item.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !item.PlatformBadge.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !item.Source.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (_libraryPlatform != "All platforms" && item.PlatformBadge != _libraryPlatform)
        {
            return false;
        }

        if (_libraryResolution != "All resolutions" && item.ResolutionLabel != _libraryResolution)
        {
            return false;
        }

        return true;
    }

    private void ApplySort()
    {
        if (RecordingView is null)
        {
            return;
        }

        using (RecordingView.DeferRefresh())
        {
            RecordingView.SortDescriptions.Clear();
            switch (_librarySort)
            {
                case "Oldest first":
                    RecordingView.SortDescriptions.Add(new SortDescription("CreatedAt", ListSortDirection.Ascending));
                    break;
                case "Largest first":
                    RecordingView.SortDescriptions.Add(new SortDescription("SizeBytes", ListSortDirection.Descending));
                    break;
                case "Longest first":
                    RecordingView.SortDescriptions.Add(new SortDescription("Duration", ListSortDirection.Descending));
                    break;
                case "Name A–Z":
                    RecordingView.SortDescriptions.Add(new SortDescription("FileName", ListSortDirection.Ascending));
                    break;
                default:
                    RecordingView.SortDescriptions.Add(new SortDescription("CreatedAt", ListSortDirection.Descending));
                    break;
            }
        }

        RecordingView.Refresh();
        Raise(nameof(LibrarySummary));
    }

    private void RefreshViewMetadata() => RecordingView?.Refresh();
}
