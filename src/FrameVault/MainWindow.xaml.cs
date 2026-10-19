using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FrameVault;

public sealed partial class MainWindow : Window
{
    private IReadOnlyList<PhotoCandidate> _scanned = [];
    private readonly SettingsStorage _settingsStorage = new();
    private CancellationTokenSource? _operationCts;
    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<BackupProfile> _profiles = [];
    private HashSet<string> _deviceSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownSourceValues = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingProfile;
    private bool _usbPromptOpen;
    private string? _selectedSourceValue;
    private string? _selectedSourceDisplay;
    private bool _settingSourceText;
    private bool _firstActivation = true;
    private string _savedPassword = "";
    private bool _destinationFolderSelected;
    private bool _settingDestination;
    private bool _loadingTheme = true;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        StartUsbPolling();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_firstActivation) return;
        _firstActivation = false;
        if (AutoDetectCheckBox.IsChecked == true)
            await PollUsbDevicesAsync(true);
        _usbTimer.Start();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _operationCts?.Cancel();
        _usbTimer.Stop();
    }

    private void StartUsbPolling()
    {
        _usbTimer.Tick += async (_, _) => await PollUsbDevicesAsync(false);
    }
    private void LoadSettings()
    {
        var document = _settingsStorage.Load();
        _profiles.AddRange(document.Profiles);
        CacheFolderBox.Text = document.CacheFolder ?? "";
        AutoDetectCheckBox.IsChecked = document.AutoDetect;
        CacheLimitBox.Value = document.CacheLimitGb <= 0 ? 50 : document.CacheLimitGb;
        var darkTheme = string.Equals(document.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        ThemeToggle.IsOn = darkTheme;
        ApplyTheme(darkTheme);
        _loadingTheme = false;
        RefreshProfileBox();
        if (_profiles.Count > 0)
        {
            var index = Math.Clamp(document.ActiveProfileIndex, 0, _profiles.Count - 1);
            ProfileBox.SelectedIndex = index;
            ApplyProfile(_profiles[index]);
        }
    }

    private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (MainRoot is null || ThemeToggle is null) return;
        if (_loadingTheme)
        {
            ApplyTheme(ThemeToggle.IsOn);
            return;
        }

        AnimateThemeChange(ThemeToggle.IsOn);
        SaveProfiles();
    }

    private void ApplyTheme(bool dark) =>
        MainRoot.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

    private void AnimateThemeChange(bool dark)
    {
        // 새 테마 위에서 이전 배경색을 투명하게 만들어 모든 표면을 부드럽게 드러낸다.
        ThemeFadeOverlay.Background = new SolidColorBrush(dark
            ? Microsoft.UI.ColorHelper.FromArgb(255, 247, 247, 249)
            : Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32));
        ThemeFadeOverlay.Opacity = 1;
        ApplyTheme(dark);

        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, ThemeFadeOverlay);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
    private void RefreshProfileBox()
    {
        _loadingProfile = true;
        var selected = ProfileBox.SelectedIndex;
        ProfileBox.ItemsSource = _profiles.Select(profile => profile.Name).ToList();
        ProfileBox.SelectedIndex = _profiles.Count == 0 ? -1 : Math.Clamp(selected, 0, _profiles.Count - 1);
        _loadingProfile = false;
    }

    private void ApplyProfile(BackupProfile profile)
    {
        var saved = profile.Settings;
        _loadingProfile = true;
        _settingDestination = true;
        ProfileNameBox.Text = profile.Name;
        EndpointBox.Text = saved.Endpoint;
        UserBox.Text = saved.UserName;
        _savedPassword = saved.Password;
        PasswordBox.Password = "";
        ProtocolBox.SelectedIndex = saved.Protocol switch { BackupProtocol.Local => 0, BackupProtocol.Smb => 1, _ => 2 };
        VerificationBox.SelectedIndex = saved.Verification == VerificationMode.Sha256 ? 1 : 0;
        LibraryBox.Text = saved.Protocol == BackupProtocol.Local ? "" : saved.LibraryFolder;
        _destinationFolderSelected = saved.Protocol == BackupProtocol.Local || !string.IsNullOrWhiteSpace(saved.LibraryFolder);
        _settingDestination = false;
        _loadingProfile = false;
        UpdateDestinationPreview();
    }
    private BackupSettings ReadSettings(bool requireSource = true, bool requireFolder = true)
    {
        var source = _selectedSourceValue ?? SourceFolderBox.Text.Trim();
        if ((requireSource && string.IsNullOrWhiteSpace(source)) || string.IsNullOrWhiteSpace(EndpointBox.Text))
            throw new ArgumentException(requireSource ? "원본과 백업 대상 루트를 입력하세요." : "백업 대상 루트를 입력하세요.");
        if (requireFolder && !_destinationFolderSelected)
            throw new ArgumentException("대상에 연결한 뒤 백업 폴더를 선택하세요.");
        if (string.IsNullOrWhiteSpace(source)) source = ".";
        var tag = ((ComboBoxItem)ProtocolBox.SelectedItem).Tag?.ToString();
        var protocol = tag switch { "Local" => BackupProtocol.Local, "Smb" => BackupProtocol.Smb, _ => BackupProtocol.WebDav };
        var password = string.IsNullOrEmpty(PasswordBox.Password) ? _savedPassword : PasswordBox.Password;
        if (protocol != BackupProtocol.Local && (string.IsNullOrWhiteSpace(UserBox.Text) || string.IsNullOrEmpty(password)))
            throw new ArgumentException("네트워크 대상 계정과 비밀번호를 입력하세요.");
        var verification = ((ComboBoxItem)VerificationBox.SelectedItem).Tag?.ToString() == "Sha256" ? VerificationMode.Sha256 : VerificationMode.Size;
        if (!string.IsNullOrWhiteSpace(CacheFolderBox.Text) && !Path.IsPathFullyQualified(CacheFolderBox.Text.Trim()))
            throw new ArgumentException("캐시 폴더는 완전한 로컬 경로로 입력하세요.");
        return new(source, protocol, EndpointBox.Text.Trim(), LibraryBox.Text.Trim(), UserBox.Text.Trim(), password,
            verification, string.IsNullOrWhiteSpace(CacheFolderBox.Text) ? null : CacheFolderBox.Text.Trim(), AutoDetectCheckBox.IsChecked == true,
            (int)Math.Clamp(CacheLimitBox.Value, 1, 2048), TakenFromPicker.Date, TakenToPicker.Date);
    }

    private void SetSource(string value, string? display = null)
    {
        _selectedSourceValue = value;
        _selectedSourceDisplay = display ?? value;
        _settingSourceText = true;
        SourceFolderBox.Text = _selectedSourceDisplay;
        _settingSourceText = false;
        _scanned = [];
    }

    private void SourceFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingSourceText || string.Equals(SourceFolderBox.Text, _selectedSourceDisplay, StringComparison.Ordinal)) return;
        _selectedSourceValue = null;
        _selectedSourceDisplay = null;
        _scanned = [];
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileBox.SelectedIndex < 0 || ProfileBox.SelectedIndex >= _profiles.Count) return;
        ApplyProfile(_profiles[ProfileBox.SelectedIndex]);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings(false);
            var name = ProfileNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = $"프로필 {_profiles.Count + 1}";
            var existing = _profiles.FindIndex(profile => string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase));
            settings = ToProfileSettings(settings);
            if (existing >= 0) _profiles[existing] = new(name, settings);
            else
            {
                if (_profiles.Count >= 4) throw new InvalidOperationException("백업 경로 프로필은 최대 4개까지 저장할 수 있습니다.");
                _profiles.Add(new(name, settings));
                existing = _profiles.Count - 1;
            }
            RefreshProfileBox();
            ProfileBox.SelectedIndex = existing;
            SaveProfiles();
            StatusText.Text = $"'{name}' 프로필을 암호화해 저장했습니다.";
        }
        catch (Exception ex) { StatusText.Text = $"실패: {ex.Message}"; }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var index = ProfileBox.SelectedIndex;
        if (index < 0 || index >= _profiles.Count) return;
        _profiles.RemoveAt(index);
        RefreshProfileBox();
        if (_profiles.Count > 0) { ProfileBox.SelectedIndex = Math.Min(index, _profiles.Count - 1); ApplyProfile(_profiles[ProfileBox.SelectedIndex]); }
        SaveProfiles();
        StatusText.Text = "프로필을 삭제했습니다.";
    }

    private void TakenDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_loadingProfile) _scanned = [];
    }
    private async Task PollUsbDevicesAsync(bool initial)
    {
        if (_operationCts is not null || _usbPromptOpen || AutoDetectCheckBox.IsChecked != true) return;
        try
        {
            var snapshot = await CameraSourceDiscovery.GetConnectedDeviceKeysAsync(CancellationToken.None);
            if (!initial && snapshot.SetEquals(_deviceSnapshot)) return;
            _deviceSnapshot = snapshot;
            var sources = await CameraSourceDiscovery.FindAsync(CancellationToken.None);
            var candidates = initial ? sources : sources.Where(source => !_knownSourceValues.Contains(source.SettingsValue)).ToList();
            _knownSourceValues = sources.Select(source => source.SettingsValue).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (candidates.Count > 0) await PromptUsbBackupAsync(candidates[0]);
        }
        catch (Exception ex) { StatusText.Text = $"USB 자동 인식 실패: {ex.Message}"; }
    }

    private static string DescribeProfile(BackupProfile profile)
    {
        var settings = profile.Settings;
        var protocol = settings.Protocol switch
        {
            BackupProtocol.Local => "로컬",
            BackupProtocol.Smb => "SMB",
            _ => "WebDAV"
        };
        var path = string.IsNullOrWhiteSpace(settings.LibraryFolder)
            ? settings.Endpoint
            : $"{settings.Endpoint.TrimEnd('\\', '/')}/{settings.LibraryFolder.Trim('\\', '/')}";
        var verification = settings.Verification == VerificationMode.Sha256 ? "정밀 검증" : "표준 검증";
        return $"{profile.Name} — {protocol} — {path} — {verification}";
    }

    private async Task PromptUsbBackupAsync(CameraSource source)
    {
        _usbPromptOpen = true;
        try
        {
            // 장치를 발견한 순간 현재 원본을 갱신한다. 팝업을 취소해도 원본은 유지된다.
            SetSource(source.SettingsValue, $"{source.DisplayName} — {source.MediaRootName}");
            StatusText.Text = $"{source.DisplayName}을 원본으로 인식했습니다.";

            if (_profiles.Count == 0)
            {
                StatusText.Text = $"{source.DisplayName}을 원본으로 인식했습니다. 먼저 백업 대상 프로필을 저장하세요.";
                return;
            }

            var selectedIndex = ProfileBox.SelectedIndex >= 0 ? ProfileBox.SelectedIndex : 0;
            var profilePicker = new ComboBox
            {
                ItemsSource = _profiles.Select(DescribeProfile).ToList(),
                SelectedIndex = selectedIndex,
                MinWidth = 520,
                Header = "이번 백업에 사용할 프로필"
            };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = $"새 USB 장치: {source.DisplayName}" });
            panel.Children.Add(new TextBlock { Text = $"사진 폴더: {source.MediaRootName}" });
            panel.Children.Add(profilePicker);

            var dialog = new ContentDialog
            {
                Title = "새 장치를 바로 백업할까요?",
                Content = panel,
                PrimaryButtonText = "선택한 프로필로 백업",
                CloseButtonText = "나중에",
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var profileIndex = profilePicker.SelectedIndex >= 0 ? profilePicker.SelectedIndex : selectedIndex;
            ProfileBox.SelectedIndex = profileIndex;
            ApplyProfile(_profiles[profileIndex]);
            Backup_Click(this, null!);
        }
        finally { _usbPromptOpen = false; }
    }
    private async void AutoDetect_Click(object sender, RoutedEventArgs e) => await RunBusy(ct => DetectSourceAsync(true, ct));

    private async Task DetectSourceAsync(bool showEmptyError, CancellationToken ct)
    {
        StatusText.Text = "USB 카메라와 메모리카드를 찾는 중입니다.";
        var sources = await CameraSourceDiscovery.FindAsync(ct);
        if (sources.Count == 0)
        {
            if (showEmptyError) throw new IOException("DCIM과 지원 미디어가 있는 카메라 또는 메모리카드를 찾지 못했습니다.");
            StatusText.Text = "연결된 카메라 또는 메모리카드가 없습니다.";
            return;
        }
        CameraSource selected;
        if (sources.Count == 1) selected = sources[0];
        else
        {
            var picker = new ComboBox { ItemsSource = sources, DisplayMemberPath = nameof(CameraSource.DisplayName), SelectedIndex = 0, MinWidth = 360 };
            var dialog = new ContentDialog { Title = "백업할 장치 선택", Content = picker, PrimaryButtonText = "선택", CloseButtonText = "취소", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { StatusText.Text = "장치 선택을 취소했습니다."; return; }
            selected = (CameraSource)picker.SelectedItem;
        }
        SetSource(selected.SettingsValue, $"{selected.DisplayName} — {selected.MediaRootName}");
        StatusText.Text = $"{selected.DisplayName}에서 {selected.MediaRootName}을 찾았습니다.";
    }

    private async void CameraInfo_Click(object sender, RoutedEventArgs e)
    {
        await RunBusy(async ct =>
        {
            var source = await ResolveSourceForInspectionAsync(ct);
            StatusText.Text = "최신 사진을 빠르게 찾는 중입니다.";
            var latest = await CameraSourceDiscovery.FindLatestPhotoAsync(source, ct)
                ?? throw new IOException("카메라 또는 메모리카드에서 사진 파일을 찾지 못했습니다.");

            StatusText.Text = $"가장 최근 사진의 카메라 정보를 읽는 중입니다: {latest.FileName}";
            CameraMetadataSummary metadata;
            await using (var stream = await latest.OpenReadAsync(ct))
                metadata = await Task.Run(() => CameraMetadataReader.Read(stream, latest), ct);

            var panel = new StackPanel { Spacing = 8, MinWidth = 470 };
            panel.Children.Add(new TextBlock
            {
                Text = $"기준 파일: {metadata.FileName}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            void AddField(string label, string value)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var labelText = new TextBlock { Text = label, Opacity = 0.75 };
                var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(valueText, 1);
                row.Children.Add(labelText);
                row.Children.Add(valueText);
                panel.Children.Add(row);
            }

            AddField("최근 촬영 일시", metadata.CaptureDate);
            AddField("총 셔터수", metadata.ShutterCount);
            AddField("카메라 기종", metadata.CameraModel);
            AddField("시리얼 번호", metadata.SerialNumber);
            AddField("카메라 바디 제조일자", metadata.BodyManufactureDate);
            panel.Children.Add(new Border { Height = 12 });
            AddField("렌즈 제조사", metadata.LensMake);
            AddField("렌즈 모델명", metadata.LensModel);
            AddField("렌즈 스펙 정보", metadata.LensInfo);

            var dialog = new ContentDialog
            {
                Title = "카메라 정보",
                Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                CloseButtonText = "닫기",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
            StatusText.Text = $"{metadata.FileName}의 카메라 정보를 확인했습니다.";
        });
    }

    private async Task<string> ResolveSourceForInspectionAsync(CancellationToken ct)
    {
        var source = _selectedSourceValue ?? SourceFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("먼저 카메라나 메모리카드 원본을 선택하세요.");
        if (!CameraSourceDiscovery.IsWpd(source)) return source;

        var sources = await CameraSourceDiscovery.FindAsync(ct);
        var current = sources.FirstOrDefault(item =>
            string.Equals(item.SettingsValue, source, StringComparison.OrdinalIgnoreCase));
        if (current is not null) return current.SettingsValue;

        var cameras = sources.Where(item => item.Kind == CameraSourceKind.Wpd).ToList();
        if (cameras.Count == 1)
        {
            var camera = cameras[0];
            SetSource(camera.SettingsValue, $"{camera.DisplayName} — {camera.MediaRootName}");
            return camera.SettingsValue;
        }
        if (cameras.Count == 0)
            throw new IOException("연결된 WPD 카메라를 찾을 수 없습니다.");
        throw new IOException("WPD 카메라가 여러 대 연결되어 있습니다. 자동 탐색에서 확인할 카메라를 선택하세요.");
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path is not null) SetSource(path);
    }

    private async void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path is not null) EndpointBox.Text = path;
    }

    private void DestinationPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_settingDestination && ReferenceEquals(sender, EndpointBox))
        {
            ResetDestinationSelection();
            if (ProtocolBox.SelectedIndex == 0) _destinationFolderSelected = true;
        }
        UpdateDestinationPreview();
    }

    private void DestinationCredentials_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingDestination) ResetDestinationSelection();
    }

    private void ResetDestinationSelection()
    {
        _destinationFolderSelected = false;
        if (LibraryBox is not null) LibraryBox.Text = "";
    }

    private async void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        await RunBusy(async ct =>
        {
            var settings = ReadSettings(false, false);
            await using var destination = DestinationFactory.Create(settings);
            StatusText.Text = "대상에 로그인하고 폴더를 조회하는 중입니다.";
            await destination.TestAsync(ct);
            var selected = await ShowDestinationTreeAsync(destination, ct);
            if (selected is null) { StatusText.Text = "대상 폴더 선택을 취소했습니다."; return; }
            LibraryBox.Text = selected.RelativePath;
            _destinationFolderSelected = true;
            SaveSettings(settings with { LibraryFolder = selected.RelativePath });
            StatusText.Text = string.IsNullOrWhiteSpace(selected.RelativePath) ? "대상 루트를 선택했습니다." : $"대상 폴더를 선택했습니다: {selected.RelativePath}";
        });
    }

    private async Task<DestinationFolder?> ShowDestinationTreeAsync(IDestination destination, CancellationToken ct)
    {
        var tree = new TreeView { SelectionMode = TreeViewSelectionMode.Single, MinWidth = 520, MinHeight = 380 };
        var root = new TreeViewNode { Content = new DestinationFolder("대상 루트", ""), IsExpanded = true };
        tree.RootNodes.Add(root);

        async Task LoadChildrenAsync(TreeViewNode node)
        {
            if (node.Content is not DestinationFolder folder || node.Children.Count > 0) return;
            var children = await destination.ListDirectoriesAsync(folder.RelativePath, ct);
            foreach (var child in children)
                node.Children.Add(new TreeViewNode { Content = child, HasUnrealizedChildren = true });
            node.HasUnrealizedChildren = false;
        }

        await LoadChildrenAsync(root);
        tree.Expanding += async (_, args) =>
        {
            try { await LoadChildrenAsync(args.Node); }
            catch (Exception ex) { StatusText.Text = $"폴더 조회 실패: {ex.Message}"; }
        };
        tree.SelectionChanged += async (_, _) =>
        {
            var node = tree.SelectedNode;
            if (node is null) return;
            try { await LoadChildrenAsync(node); node.IsExpanded = true; }
            catch (Exception ex) { StatusText.Text = $"폴더 조회 실패: {ex.Message}"; }
        };
        tree.SelectedNode = root;
        var dialog = new ContentDialog
        {
            Title = "백업 대상 폴더 선택",
            Content = tree,
            PrimaryButtonText = "이 폴더 선택",
            CloseButtonText = "취소",
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return tree.SelectedNode?.Content as DestinationFolder;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointLabel is null) return;
        var local = ProtocolBox.SelectedIndex == 0;
        var smb = ProtocolBox.SelectedIndex == 1;
        EndpointLabel.Text = local ? "백업 대상 폴더 — 로컬 폴더/외장 드라이브" : smb ? "백업 대상 루트 — SMB 공유 경로" : "백업 대상 루트 — HTTPS WebDAV URL";
        EndpointBox.PlaceholderText = local ? @"예: D:\CameraBackup" : smb ? @"예: \\diskstation\photo" : "예: https://nas.example.com:5006/photo";
        CredentialsPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        PickDestinationButton.Visibility = local || smb ? Visibility.Visible : Visibility.Collapsed;
        DestinationFolderPanel.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        if (!_settingDestination)
        {
            ResetDestinationSelection();
            if (local) _destinationFolderSelected = true;
        }
        UpdateDestinationPreview();
    }

    private void UpdateDestinationPreview()
    {
        if (DestinationPreviewText is null || EndpointBox is null || LibraryBox is null || ProtocolBox is null) return;
        var root = EndpointBox.Text.TrimEnd('\\', '/');
        var subfolder = LibraryBox.Text.Trim().Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(root)) { DestinationPreviewText.Text = "대상 루트 경로를 입력하세요."; return; }
        if (!_destinationFolderSelected) { DestinationPreviewText.Text = "대상에 연결한 뒤 폴더를 선택하세요."; return; }
        var separator = ProtocolBox.SelectedIndex == 2 ? "/" : "\\";
        DestinationPreviewText.Text = string.IsNullOrWhiteSpace(subfolder)
            ? $"{root}{separator}YYYY{separator}MM{separator}원본파일명"
            : $"{root}{separator}{subfolder.Replace("/", separator).Replace("\\", separator)}{separator}YYYY{separator}MM{separator}원본파일명";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        await RunBusy(async ct =>
        {
            var settings = ReadSettings(false, false);
            await using var destination = DestinationFactory.Create(settings);
            await destination.TestAsync(ct);
            SaveSettings(settings);
            StatusText.Text = "대상 연결에 성공했습니다. 설정을 현재 Windows 사용자 범위로 암호화해 저장했습니다.";
        });
    }

    private async Task<BackupSettings> ResolveCurrentSourceAsync(BackupSettings settings, CancellationToken ct)
    {
        if (!CameraSourceDiscovery.IsWpd(settings.SourceFolder)) return settings;
        var sources = await CameraSourceDiscovery.FindAsync(ct);
        var current = sources.FirstOrDefault(source => string.Equals(source.SettingsValue, settings.SourceFolder, StringComparison.OrdinalIgnoreCase));
        if (current is not null) return settings;

        var connectedCameras = sources.Where(source => source.Kind == CameraSourceKind.Wpd).ToList();
        if (connectedCameras.Count == 1)
        {
            var camera = connectedCameras[0];
            SetSource(camera.SettingsValue, $"{camera.DisplayName} — {camera.MediaRootName}");
            StatusText.Text = $"USB 재연결로 변경된 {camera.DisplayName} 장치 ID를 자동으로 갱신했습니다.";
            return settings with { SourceFolder = camera.SettingsValue };
        }
        if (connectedCameras.Count == 0)
            throw new IOException("저장된 WPD 카메라를 찾을 수 없습니다. 카메라를 다시 연결하거나 자동 탐색을 실행하세요.");
        throw new IOException("연결된 WPD 카메라가 여러 대입니다. 자동 탐색에서 백업할 카메라를 선택하세요.");
    }
    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configured = CacheFolderBox.Text.Trim();
            if (!string.IsNullOrEmpty(configured) && !Path.IsPathFullyQualified(configured))
                throw new ArgumentException("캐시 폴더는 완전한 로컬 경로로 입력하세요.");
            var cacheBase = string.IsNullOrEmpty(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameVault", "WpdCache")
                : Path.GetFullPath(configured);
            Directory.CreateDirectory(cacheBase);
            var startInfo = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(cacheBase);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) { StatusText.Text = $"캐시 폴더를 열 수 없습니다: {ex.Message}"; }
    }
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is not null) return;
        var cacheBase = string.IsNullOrWhiteSpace(CacheFolderBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameVault", "WpdCache")
            : Path.GetFullPath(CacheFolderBox.Text.Trim());
        var folders = Directory.Exists(cacheBase)
            ? Directory.EnumerateDirectories(cacheBase)
                .Where(path => Path.GetFileName(path).Length == 20 && Path.GetFileName(path).All(Uri.IsHexDigit)).ToList()
            : [];
        if (folders.Count == 0) { StatusText.Text = "삭제할 카메라 임시 파일이 없습니다."; return; }
        var bytes = folders.Sum(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length));
        var dialog = new ContentDialog
        {
            Title = "카메라 임시 파일 삭제",
            Content = new TextBlock { Text = $"앱이 만든 임시 파일 {bytes / 1024d / 1024 / 1024:F2}GB를 삭제할까요? 새 검사와 백업은 카메라 파일을 직접 읽습니다.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "삭제", CloseButtonText = "취소", XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunBusy(async ct =>
        {
            await Task.Run(() =>
            {
                foreach (var folder in folders) { ct.ThrowIfCancellationRequested(); Directory.Delete(folder, true); }
            }, ct);
            _scanned = [];
            FilesList.ItemsSource = null;
            StatusText.Text = $"카메라 임시 파일 {bytes / 1024d / 1024 / 1024:F2}GB를 삭제했습니다.";
        });
    }

    private async void IntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        await RunBusy(async ct =>
        {
            var settings = await ResolveCurrentSourceAsync(ReadSettings(), ct);
            var progress = new Progress<string>(message => StatusText.Text = message);
            var scanned = CameraSourceDiscovery.IsWpd(settings.SourceFolder)
                ? await CameraSourceDiscovery.ScanWpdAsync(CameraSourceDiscovery.WpdId(settings.SourceFolder), progress, ct)
                : await Task.Run(() => PhotoScanner.Scan(settings.SourceFolder), ct);
            scanned = ApplyDateFilter(scanned, settings);
            await using var destination = DestinationFactory.Create(settings);
            await destination.TestAsync(ct);
            var missing = new List<string>();
            var mismatched = new List<string>();
            var checkedCount = 0;
            UpdateBackupProgress(0, scanned.Count);
            foreach (var item in scanned)
            {
                ct.ThrowIfCancellationRequested();
                var hash = await Hashing.Sha256Async(item, ct);
                var result = await destination.VerifyIntegrityAsync(item, hash, ct);
                if (!result.Exists) missing.Add(item.RelativePath);
                else if (!result.Matches) mismatched.Add(item.RelativePath);
                UpdateBackupProgress(++checkedCount, scanned.Count);
                StatusText.Text = $"전체 무결성 검사 중: {checkedCount:N0}/{scanned.Count:N0} — 누락 {missing.Count:N0}, 불일치 {mismatched.Count:N0}";
            }
            FilesList.ItemsSource = missing.Select(path => $"누락: {path}").Concat(mismatched.Select(path => $"불일치: {path}")).ToList();
            StatusText.Text = $"검사 완료: {checkedCount:N0}개 중 누락 {missing.Count:N0}개, 불일치 {mismatched.Count:N0}개. 이 검사는 파일을 변경하지 않았습니다.";
        });
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunBusy(async ct =>
        {
            var settings = await ResolveCurrentSourceAsync(ReadSettings(), ct);
            SaveSettings(settings);
            var missingStagedFile = _scanned.Any(item => item.IsStaged && !File.Exists(item.FullPath));
            if (_scanned.Count == 0 || missingStagedFile)
            {
                var progress = new Progress<string>(message => StatusText.Text = message);
                if (missingStagedFile) StatusText.Text = "삭제된 WPD 캐시 파일을 카메라에서 다시 가져오는 중입니다.";
                _scanned = CameraSourceDiscovery.IsWpd(settings.SourceFolder)
                    ? await CameraSourceDiscovery.ScanWpdAsync(CameraSourceDiscovery.WpdId(settings.SourceFolder), progress, ct)
                    : await Task.Run(() => PhotoScanner.Scan(settings.SourceFolder), ct);
            }
            _scanned = ApplyDateFilter(_scanned, settings);
            if (settings.TakenFrom is not null || settings.TakenTo is not null)
                StatusText.Text = $"촬영 기간에 해당하는 {_scanned.Count:N0}개 파일을 백업 대상으로 선택했습니다.";
            using var log = new BackupLog();
            await using var destination = DestinationFactory.Create(settings);
            await destination.TestAsync(ct);
            var completionRecords = _scanned.Select(item => (Item: item, Hash: log.GetCompletedHash(item, destination.Id))).ToList();
            var verified = new bool[completionRecords.Count];
            var checkedCount = 0;
            var sourceParallelism = CameraSourceDiscovery.IsWpd(settings.SourceFolder) ? 1 : 4;
            using (var gate = new SemaphoreSlim(sourceParallelism))
            {
                var checks = completionRecords.Select(async (record, index) =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var hash = settings.Verification == VerificationMode.Sha256
                            ? await HashSourceWithRetryAsync(record.Item, ct)
                            : record.Hash;
                        if (hash is not null)
                            verified[index] = await destination.VerifyExistingAsync(record.Item, hash, ct);
                    }
                    finally
                    {
                        gate.Release();
                        var current = Interlocked.Increment(ref checkedCount);
                        DispatcherQueue.TryEnqueue(() => StatusText.Text = $"{(settings.Verification == VerificationMode.Sha256 ? "원본·대상 해시 대조 중" : "서버 파일 빠른 대조 중")}: {current:N0}/{completionRecords.Count:N0}");
                    }
                });
                await Task.WhenAll(checks);
            }
            var pending = completionRecords.Where((_, index) => !verified[index]).Select(record => record.Item).ToList();
            UpdateBackupProgress(0, pending.Count);
            var completed = 0;
            var parallelUploads = CameraSourceDiscovery.IsWpd(settings.SourceFolder) ? 1 : 2;
            for (var offset = 0; offset < pending.Count; offset += parallelUploads)
            {
                ct.ThrowIfCancellationRequested();
                var batch = pending.Skip(offset).Take(parallelUploads).ToList();
                StatusText.Text = $"{completed:N0}개 파일 전송 완료 ({completed}/{pending.Count}) — {batch.Count}개 병렬 전송 중";
                foreach (var item in batch)
                    log.MarkState(item, destination.Id, TransferState.Uploading);

                var tasks = batch.Select(async item =>
                {
                    try
                    {
                        var hash = await HashSourceWithRetryAsync(item, ct);
                        var transfer = await RetryAsync(() => destination.UploadAtomicAsync(item, hash, ct), ct);
                        return (Item: item, Hash: hash, Transfer: transfer, Error: (Exception?)null);
                    }
                    catch (Exception ex)
                    {
                        return (Item: item, Hash: (string?)null, Transfer: (TransferResult?)null, Error: ex);
                    }
                });

                var results = await Task.WhenAll(tasks);
                foreach (var result in results)
                {
                    if (result.Error is not null)
                    {
                        log.MarkState(result.Item, destination.Id, TransferState.Failed,
                            $"{result.Error.GetType().Name}: {result.Error.Message}");
                        throw result.Error;
                    }
                    log.MarkState(result.Item, destination.Id, TransferState.RemoteVerified, result.Transfer!.Verification);
                    log.Complete(result.Item, destination.Id, result.Transfer, result.Hash!);
                    UpdateBackupProgress(++completed, pending.Count);
                }
            }            foreach (var staged in _scanned.Where(item => item.IsStaged))
                TryDeleteStagedFile(staged.FullPath);
            _scanned = [];
            StatusText.Text = pending.Count == 0 ? "새 파일이 없습니다." : $"{completed:N0}개 파일의 전송과 원격 검증을 완료했습니다.";
        });
    }

    private void UpdateBackupProgress(int completed, int total)
    {
        var percent = total == 0 ? 100 : (int)Math.Round(completed * 100d / total);
        Progress.Maximum = 100;
        Progress.Value = percent;
        ProgressText.Text = $"{percent}%";
        StatusText.Text = total == 0 ? "새 파일이 없습니다." : $"{completed:N0}개 파일 전송 완료 ({completed}/{total})";
    }
    private static IReadOnlyList<PhotoCandidate> ApplyDateFilter(IReadOnlyList<PhotoCandidate> items, BackupSettings settings)
    {
        var from = settings.TakenFrom?.Date;
        var toExclusive = settings.TakenTo?.Date.AddDays(1);
        if (from is not null && toExclusive is not null && from >= toExclusive)
            throw new ArgumentException("촬영 기간의 시작일은 종료일보다 늦을 수 없습니다.");
        return items.Where(item =>
        {
            var captured = item.TakenAt.Date;
            return (from is null || captured >= from.Value) &&
                   (toExclusive is null || captured < toExclusive.Value);
        }).ToList();
    }
    private static void TryDeleteStagedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static BackupSettings ToProfileSettings(BackupSettings settings) => settings with
    {
        SourceFolder = "",
        CacheFolder = null,
        AutoDetect = true,
        CacheLimitGb = 50,
        TakenFrom = null,
        TakenTo = null
    };

    private void SaveSettings(BackupSettings settings)
    {
        _savedPassword = settings.Password;
        var profileSettings = ToProfileSettings(settings);
        var index = ProfileBox.SelectedIndex;
        if (index >= 0 && index < _profiles.Count)
            _profiles[index] = _profiles[index] with { Settings = profileSettings };
        else if (_profiles.Count < 4)
        {
            _profiles.Add(new("기본 프로필", profileSettings));
            RefreshProfileBox();
            ProfileBox.SelectedIndex = _profiles.Count - 1;
        }
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        var active = ProfileBox.SelectedIndex < 0 ? 0 : ProfileBox.SelectedIndex;
        _settingsStorage.Save(new(4, [.. _profiles], active,
            string.IsNullOrWhiteSpace(CacheFolderBox.Text) ? null : CacheFolderBox.Text.Trim(),
            AutoDetectCheckBox.IsChecked == true,
            (int)Math.Clamp(CacheLimitBox.Value, 1, 2048),
            ThemeToggle.IsOn ? "Dark" : "Light"));
    }
    private static Task<string> HashSourceWithRetryAsync(PhotoCandidate item, CancellationToken ct) =>
        RetryAsync(() => Hashing.Sha256Async(item, ct), ct);

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return await action(); }
            catch (BackupConflictException) { throw; }
            catch (Exception ex) when (attempt < 2) { last = ex; await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
        throw new IOException("3회 재시도 후 업로드에 실패했습니다.", last);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async Task RunBusy(Func<CancellationToken, Task> action)
    {
        if (_operationCts is not null) return;
        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        try
        {
            SetBusy(true);
            await action(cts.Token);
        }
        catch (OperationCanceledException) { StatusText.Text = "작업을 안전하게 취소했습니다. 완료되지 않은 .part 파일은 다음 재시도에 사용할 수 있습니다."; }
        catch (Exception ex) { StatusText.Text = $"실패: {ex.Message}"; }
        finally { _operationCts = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        DetectButton.IsEnabled = !busy; PickSourceButton.IsEnabled = !busy; CameraInfoButton.IsEnabled = !busy; TestButton.IsEnabled = !busy;
        IntegrityButton.IsEnabled = !busy; ClearCacheButton.IsEnabled = !busy; OpenCacheButton.IsEnabled = !busy; BackupButton.IsEnabled = !busy; BrowseDestinationButton.IsEnabled = !busy; CancelButton.IsEnabled = busy;
    }
}







