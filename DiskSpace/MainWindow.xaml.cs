using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DiskSpace;

public partial class MainWindow : Window
{
    private readonly LocalStore store = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, ScanResult> scans = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Volume> seenVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Page, Button Button)> nav = [];
    private readonly ManualResetEventSlim pause = new(true);
    private readonly Forms.NotifyIcon tray;
    private List<Snapshot> history;
    private List<CleanupLog> journal;
    private Preferences settings;
    private Volume[] volumes = [];
    private Candidate[]? candidates;
    private DuplicateRow[]? duplicateRows;
    private CancellationTokenSource? current;
    private DateTimeOffset lastBackground;
    private DateTimeOffset lastCleanupCheck;
    private CleanupSchedule autoReport;
    private bool cleanupOpening;
    private bool cleanupRulesChanged;
    private CancellationTokenSource? backgroundScan;
    private bool backgroundAnalysis;
    private string active = "Обзор";
    private string? selectedVolume;
    private string? selectedFolder;
    private string? preferredScope;
    private string search = "";
    private long? plannerNeed;
    private bool leaving;
    private bool demo;
    private bool showFiles;
    private bool freeView;
    private bool showMap;
    private int historyDays = 30;
    private int beforeChoice;
    private int afterChoice = -1;
    private bool Motion => settings.Animate && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && WindowState != WindowState.Minimized;
    private string[] Exclusions => settings.Excluded ?? [];
    private Volume? Selected => volumes.FirstOrDefault(item => item.Id == selectedVolume) ?? volumes.FirstOrDefault(item => item.Ready);

    public MainWindow()
    {
        InitializeComponent();
        demo = Environment.GetCommandLineArgs().Contains("--demo");
        var previous = demo ? new Preferences() : store.Read("settings.json", new Preferences());
        cleanupRulesChanged = previous.AutoClean && previous.CleanupConsent < 2;
        settings = previous with { AutoClean = previous.AutoClean && !cleanupRulesChanged, MinimumAge = Math.Clamp(previous.MinimumAge, 1, 365), LimitMb = Math.Clamp(previous.LimitMb, 1, 10240) };
        history = demo ? [] : store.Read("history.json", new List<Snapshot>());
        journal = demo ? [] : store.Read("cleanup.json", new List<CleanupLog>());
        lastBackground = demo ? DateTimeOffset.MinValue : store.Read("background.json", DateTimeOffset.MinValue);
        autoReport = demo ? CleanupSchedule.Empty : store.Read("auto-cleanup.json", CleanupSchedule.Empty);
        LoadUsage();
        ApplyTheme();
        foreach (var (title, icon) in new[] { ("Обзор", "\uE80F"), ("Анализ", "\uE8B7"), ("История", "\uE9D9"), ("Очистка", "\uE74D"), ("Ревизия", "\uE8A9"), ("Настройки", "\uE713") })
        {
            var row = Row();
            row.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons"), Width = 30, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 });
            row.Children.Add(Text(title, 14));
            var button = ActionButton(title, () => Go(title));
            AutomationProperties.SetName(button, title);
            button.Content = row;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, 4);
            nav.Add((title, button));
            Navigation.Children.Add(button);
        }
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Куда делась память?" };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        tray.ContextMenuStrip.Items.Add("Приостановить наблюдение", null, (_, _) => Dispatcher.Invoke(() => { Save(settings with { Watch = false }); tray.Visible = false; ShowFromTray(); }));
        tray.ContextMenuStrip.Items.Add("Выйти", null, (_, _) => Dispatcher.Invoke(() => { leaving = true; Close(); }));
        SourceInitialized += (_, _) => ApplyBackdrop();
        Loaded += async (_, _) =>
        {
            if (demo) SetupDemo();
            Render();
            SetStatus(store.Error ?? (cleanupRulesChanged ? "Для автоочистки подтвердите обновлённые правила" : "Готово к анализу"));
            if (!demo) await RefreshVolumes();
            if (Environment.GetCommandLineArgs().Contains("--tray") && settings.Watch && !demo) HideToTray();
            timer.Start();
        };
        timer.Tick += async (_, _) => await BackgroundTick();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchClick(this, new RoutedEventArgs());
                args.Handled = true;
            }
        };
        Closing += (_, args) =>
        {
            if (!leaving && settings.Watch && !demo) { args.Cancel = true; HideToTray(); return; }
            current?.Cancel();
            timer.Stop();
            tray.Dispose();
        };
    }

    private async Task RefreshVolumes(bool refreshView = true)
    {
        try
        {
            if (demo) return;
            var actual = await Task.Run(DiskScanner.Volumes);
            foreach (var volume in actual) seenVolumes[volume.Id] = volume;
            var present = actual.Select(volume => volume.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            volumes = actual.Concat(seenVolumes.Values.Where(volume => !present.Contains(volume.Id))
                .Select(volume => volume with { Ready = false })).OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase).ToArray();
            if (refreshView && active == "Обзор") Render();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetStatus("Не удалось обновить список дисков");
        }
    }


    private async Task BackgroundTick()
    {
        if (demo || ticking) return;
        ticking = true;
        try
        {
            await ObserveAppsAsync();
            if (current is not null) return;
            if (settings.Watch && Idle(60_000))
            {
                var now = DateTimeOffset.Now;
                if (settings.AutoClean && autoReport.Due(now))
                    await RunAutoCleanup(force: false);
                else if (!settings.AutoClean && now - lastCleanupCheck >= TimeSpan.FromHours(6))
                {
                    lastCleanupCheck = now;
                    if (await FindCleanup(automatic: true) && settings.Notify && candidates is { Length: > 0 })
                        Notify($"Можно освободить до {Bytes(candidates.Sum(item => item.Bytes))}");
                }
            }

            if (current is not null) return;
            if (active == "Обзор" || settings.Watch) await RefreshVolumes();
            if (!settings.Watch || !Idle() || active is "Очистка" or "Ревизия" || current is not null
                || Forms.SystemInformation.PowerStatus.PowerLineStatus != Forms.PowerLineStatus.Online) return;
            var low = volumes.FirstOrDefault(v => v.Ready && (v.Fill > 90 || v.Free < 10L * 1024 * 1024 * 1024));
            if (DateTimeOffset.Now - lastBackground < TimeSpan.FromHours(low is not null ? 6 : 24)) return;
            lastBackground = DateTimeOffset.Now;
            TrySave(() => store.Save("background.json", lastBackground));
            if (low is not null && settings.Notify) Notify($"{low.Title} · свободно {Bytes(low.Free)}");
            backgroundAnalysis = true;
            try { await ScanVolumes(volumes.Where(v => v.Ready).ToArray(), automatic: true); }
            finally { backgroundAnalysis = false; }
        }
        finally { ticking = false; }
    }

    private void Notify(string message)
    {
        tray.Visible = true;
        tray.ShowBalloonTip(5000, "Куда делась память?", message, Forms.ToolTipIcon.Info);
    }

    private bool ticking;

    private async Task<bool> ScanVolumes(Volume[] targets, string? chosen = null, bool automatic = false)
    {
        if (targets.Length == 0) { SetStatus("Нет доступных дисков"); return false; }
        var completed = await RunWork(automatic ? "Фоновый анализ…" : "Анализ дисков…", async token =>
        {
            if (demo) { await Task.Delay(180, token); return; }
            foreach (var volume in targets)
            {
                var scope = chosen ?? volume.Root;
                var reporter = new Progress<ScanProgress>(report =>
                {
                    if (current is not null) SetStatus($"{report.Files:N0} файлов · {report.Skipped:N0} пропусков · {report.Path}");
                });
                var result = await Task.Run(() => DiskScanner.Scan(scope, pause, token, reporter), token);
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(scope)) throw new DirectoryNotFoundException(scope);
                if (!NativeFiles.FreeSpace(volume.Root, out var capacity, out var free)) throw new IOException("Диск отключён");
                scans[volume.Id] = result;
                history.Add(LocalStore.MakeSnapshot(volume with { Capacity = capacity, Free = free }, result));
                SaveHistory();
            }
        }, pausable: true);
        await RefreshVolumes(false);
        if (completed) SetStatus($"Анализ завершён · дисков {targets.Length}");
        if (active == "Анализ" || active == "Обзор" || active == "История") Render();
        return completed;
    }

    private async Task<bool> RunWork(string label, Func<CancellationToken, Task> work, bool pausable = false)
    {
        if (current is not null) { SetStatus("Дождитесь завершения текущей операции"); return false; }
        using var cancel = new CancellationTokenSource();
        current = cancel;
        if (backgroundAnalysis) backgroundScan = cancel;
        pause.Set();
        SetStatus(label);
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.Visibility = pausable ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = "Пауза";
        try
        {
            await work(cancel.Token);
            return true;
        }
        catch (OperationCanceledException) { SetStatus("Операция отменена"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        { SetStatus(error.Message); }
        finally
        {
            current = null;
            if (ReferenceEquals(backgroundScan, cancel)) backgroundScan = null;
            CancelButton.Visibility = Visibility.Collapsed;
            PauseButton.Visibility = Visibility.Collapsed;
        }
        return false;
    }

    private void TogglePause(object sender, RoutedEventArgs args)
    {
        if (pause.IsSet) { pause.Reset(); PauseButton.Content = "Продолжить"; SetStatus("Анализ на паузе"); }
        else { pause.Set(); PauseButton.Content = "Пауза"; SetStatus("Анализ продолжен"); }
    }

    private void CancelWork(object sender, RoutedEventArgs args) => current?.Cancel();

    private void SaveHistory()
    {
        history = history.Where(item => item.Time > DateTimeOffset.Now.AddDays(-90)).OrderBy(item => item.Time).TakeLast(400).ToList();
        TrySave(() => store.Save("history.json", history));
    }

    private void TrySave(Action save)
    {
        if (demo) return;
        try { save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { SetStatus("Не удалось сохранить локальные данные. Проверьте свободное место и доступ."); }
    }

    private void Save(Preferences value)
    {
        settings = value;
        TrySave(() => store.Save("settings.json", value));
        ApplyTheme();
        Render();
    }

    private void Go(string page, bool modeChanged = false)
    {
        if (page == "Очистка" && backgroundAnalysis) backgroundScan?.Cancel();
        var changed = active != page || modeChanged;
        active = page;
        Render(changed);
        if (changed) Viewport.ScrollToTop();
        if (page == "Очистка" && (candidates is null || previewAutomatic) && !cleanupOpening) _ = OpenCleanupAsync();
        if (page == "Ревизия" && reviewFiles is null) _ = LoadReviewAsync();
    }

    private void OpenVolume(Volume volume)
    {
        selectedVolume = volume.Id;
        selectedFolder = null;
        preferredScope = null;
        showFiles = false;
        beforeChoice = 0;
        afterChoice = -1;
        Go("Анализ", true);
    }

    private void Render(bool transition = false)
    {
        Page.Children.Clear();
        ContextLabel.Text = demo ? "Демонстрационные данные  /  " + active : "Хранилище  /  " + active;
        WatchLabel.Text = demo ? "Демонстрационный режим" : settings.Watch ? "Фоновое наблюдение включено" : "Фоновое наблюдение выключено";
        foreach (var (title, button) in nav)
        {
            button.SetResourceReference(BackgroundProperty, title == active ? "Hover" : "Side");
            button.SetResourceReference(BorderBrushProperty, title == active ? "Line" : "Side");
        }
        switch (active)
        {
            case "Анализ": RenderAnalysis(); break;
            case "История": RenderHistory(); break;
            case "Очистка": RenderCleanup(); break;
            case "Ревизия": RenderReview(); break;
            case "Настройки": RenderSettings(); break;
            case "Поиск": RenderSearch(); break;
            case "Дубликаты": RenderDuplicates(); break;
            default: RenderOverview(); break;
        }
        if (transition && Motion && IsVisible)
        {
            Page.BeginAnimation(OpacityProperty, new DoubleAnimation(.42, 1, TimeSpan.FromMilliseconds(160)));
            var shift = new TranslateTransform();
            Page.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(190))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    internal void ActivateFromLaunch() => ShowFromTray();

    private void HideToTray() { tray.Visible = true; Hide(); }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void SetStatus(string text) => StatusLabel.Text = text;
    private void SearchClick(object sender, RoutedEventArgs args) => Go("Поиск", true);

    private void ApplyTheme()
    {
        var dark = settings.Theme == "Тёмная" || (settings.Theme == "Системная" && IsSystemDark());
        string[] keys = ["AppBg", "Side", "Card", "Raised", "Line", "Text", "Muted", "Accent", "OnAccent", "Hover", "Warning"];
        string[] values = dark ? ["#18191B", "#121315", "#212225", "#292A2E", "#3A3B40", "#F1F1F2", "#B1B3B9", "#A8CEFF", "#123059", "#303743", "#F0C489"]
            : ["#F2F3F5", "#EBEEF2", "#FCFCFD", "#F2F4F7", "#D9DCE1", "#1A2030", "#515A68", "#234E83", "#FFFFFF", "#DCE7F6", "#7D4B0B"];
        for (var i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
        if (SystemParameters.HighContrast)
        {
            var system = SystemColors.WindowBrush;
            Brush[] high = [system, system, system, system, SystemColors.WindowTextBrush, SystemColors.WindowTextBrush,
                SystemColors.WindowTextBrush, SystemColors.HighlightBrush, SystemColors.HighlightTextBrush,
                SystemColors.ControlBrush, SystemColors.WindowTextBrush];
            for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = high[i];
        }
        ApplyBackdrop();
    }

    private static bool IsSystemDark()
    {
        try { return (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) != 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private void ApplyBackdrop()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = settings.Theme == "Тёмная" || (settings.Theme == "Системная" && IsSystemDark());
        int mode = dark && !SystemParameters.HighContrast ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref mode, 4);
        int mica = SystemParameters.HighContrast ? 1 : 2;
        DwmSetWindowAttribute(handle, 38, ref mica, 4);
    }

    private static bool Idle(uint threshold = 180_000)
    {
        var info = new LastInput { Size = 8 };
        return GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount - info.Time) > threshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static string Bytes(long bytes)
    {
        var value = (double)bytes;
        string[] names = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ"];
        var index = 0;
        while (Math.Abs(value) >= 1024 && index < names.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {names[index]}";
    }

    private static string Diff(long bytes) => (bytes > 0 ? "+" : "") + Bytes(bytes);
    private static string Date(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM HH:mm");

    private Brush Brush(string key) => (Brush)FindResource(key);
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    private static WrapPanel Actions() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 13) };

    private static TextBlock Text(string value, double size = 14, string key = "Text", bool bold = false)
    {
        var label = new TextBlock { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, key);
        return label;
    }

    private static void AddText(Panel panel, string value, string color = "Muted", Thickness margin = default)
    {
        var label = Text(value, 13, color);
        label.TextWrapping = TextWrapping.Wrap;
        label.Margin = margin;
        panel.Children.Add(label);
    }

    private static Button ActionButton(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 7, 5) };
        if (primary)
        {
            button.SetResourceReference(BackgroundProperty, "Accent");
            button.SetResourceReference(ForegroundProperty, "OnAccent");
            button.SetResourceReference(BorderBrushProperty, "Accent");
        }
        button.Click += (_, _) => action();
        return button;
    }

    private Border Card(UIElement content, Thickness? padding = null)
    {
        return new Border { Background = Brush("Card"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(20), Child = content, Margin = new Thickness(0, 0, 0, 16) };
    }

    private void Heading(string title, string subtitle)
    {
        Page.Children.Add(Text(title, 32, bold: true));
        AddText(Page, subtitle, margin: new Thickness(0, 4, 0, 17));
    }

    private static DataGrid Table<T>(IEnumerable<T> rows, params (string Title, string Property, double Width)[] columns)
    {
        var table = new DataGrid { ItemsSource = rows, IsReadOnly = true,
            MaxHeight = 620, MinHeight = 70, Margin = new Thickness(0, 0, 0, 12), SelectionMode = DataGridSelectionMode.Single };
        foreach (var (title, property, width) in columns)
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width <= 0 ? DataGridLength.Auto : new DataGridLength(width) });
        return table;
    }

    private static void OpenExplorer(string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = true };
        if (Directory.Exists(path)) info.ArgumentList.Add(path);
        else { info.ArgumentList.Add("/select," + path); }
        Process.Start(info);
    }
}
