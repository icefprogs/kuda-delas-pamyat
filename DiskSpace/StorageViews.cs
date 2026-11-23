using System.Globalization;
using System.Text;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskSpace;

public partial class MainWindow
{
    private void RenderOverview()
    {
        Heading("Обзор дисков", "Свободное место на локальных дисках и последние изменения");
        var ready = volumes.Where(volume => volume.Ready).ToArray();
        var capacity = ready.Sum(volume => volume.Capacity);
        var occupied = ready.Sum(volume => volume.Used);
        var free = ready.Sum(volume => volume.Free);

        var controls = Actions();
        controls.Children.Add(ActionButton("Анализировать все диски", async () => await ScanVolumes(ready), true));
        controls.Children.Add(ActionButton("Обновить", async () => { await RefreshVolumes(); SetStatus("Сводка обновлена"); }));
        controls.Children.Add(ActionButton("Хватит ли места?", Planner));
        Page.Children.Add(controls);

        if (volumes.Length == 0)
        {
            var empty = new StackPanel();
            AddText(empty, "Получаем сведения о локальных дисках…");
            Page.Children.Add(Card(empty));
            return;
        }

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var donutLabel = Text("Всё хранилище", 17, bold: true);
        left.Children.Add(donutLabel);
        AddText(left, $"Общий объём {Bytes(capacity)}");
        var pieces = ready.Select((volume, index) => new RingPart($"{volume.Title} · {volume.Fill:0}% занято",
            volume.Used, new SolidColorBrush(Plot.Colors[index % Plot.Colors.Length]), () => OpenVolume(volume))).ToList();
        pieces.Add(new("Свободно на всех дисках · " + Bytes(free), free, Brush("Line"), () => { freeView = !freeView; Render(); }));
        var ring = new DiskRing(pieces, Bytes(occupied), "Свободно " + Bytes(free), Motion)
        { Height = 242, MaxWidth = 320 };
        AutomationProperties.SetName(ring, $"Занято {Bytes(occupied)}. Свободно {Bytes(free)}");
        left.Children.Add(ring);
        left.Children.Add(ActionButton(freeView ? "Показать все диски" : "Где есть свободное место?", () => { freeView = !freeView; Render(); }));
        layout.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(Text(freeView ? "Резерв по дискам" : "Локальные диски", 17, bold: true));
        AddText(right, $"{ready.Length} доступно" + (volumes.Length > ready.Length ? $" · {volumes.Length - ready.Length} недоступно" : ""),
            margin: new Thickness(0, 1, 0, 9));
        IEnumerable<Volume> ordered = freeView ? ready.OrderByDescending(volume => volume.Free) : volumes;
        foreach (var volume in ordered)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 5) };
            var button = ActionButton(volume.Title.Length == 0 ? volume.Root : volume.Title, () => OpenVolume(volume));
            button.Margin = new Thickness();
            button.Padding = new Thickness(0);
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            var title = Row();
            var index = Array.IndexOf(ready, volume);
            title.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 1, 8, 0), Background = index >= 0 ? new SolidColorBrush(Plot.Colors[index % Plot.Colors.Length]) : Brush("Line") });
            var titleText = Text(volume.Title, 14, bold: true);
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(titleText);
            button.Content = title;
            AutomationProperties.SetName(button, $"{volume.Title}, {volume.Fill:0}% занято");
            panel.Children.Add(button);
            if (volume.Ready)
            {
                AddText(panel, $"{Bytes(volume.Free)} свободно из {Bytes(volume.Capacity)}  ·  {volume.Fill:0}%",
                    volume.Free < 10L * 1024 * 1024 * 1024 || volume.Fill > 90 ? "Warning" : "Muted");
                panel.Children.Add(new ProgressBar { Height = 4, Maximum = 100, Value = volume.Fill,
                    Margin = new Thickness(0, 6, 0, 4), Foreground = index >= 0 ? new SolidColorBrush(Plot.Colors[index % Plot.Colors.Length]) : Brush("Accent"),
                    Background = Brush("Line"), BorderThickness = new Thickness(0) });
            }
            else AddText(panel, "Отключён или недоступен");

            if (plannerNeed is { } need && volume.Ready)
                AddText(panel, volume.Free >= need ? "Места достаточно" : $"Нужно освободить {Bytes(need - volume.Free)}", volume.Free >= need ? "Accent" : "Warning");
            right.Children.Add(panel);
        }
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        Page.Children.Add(Card(layout));

        var glance = new Grid();
        glance.ColumnDefinitions.Add(new ColumnDefinition());
        glance.ColumnDefinitions.Add(new ColumnDefinition());
        var disk = Selected;
        var growth = new StackPanel();
        growth.Children.Add(Text("Что выросло", 17, bold: true));
        var points = disk is null ? [] : Points(disk.Id);
        var largest = points.Length > 1 ? Changes(points[0], points[^1]).FirstOrDefault(r => r.Delta > 0) : null;
        AddText(growth, largest is null ? "Сравнение появится после двух замеров одной папки." : $"{largest.Path} · {largest.Size}",
            margin: new Thickness(0, 6, 0, 8));
        growth.Children.Add(ActionButton("Открыть историю", () => Go("История")));
        var growthCard = Card(growth);
        growthCard.Margin = new Thickness(0, 0, 7, 0);
        glance.Children.Add(growthCard);
        var tidy = new StackPanel();
        tidy.Children.Add(Text("Сколько можно освободить", 17, bold: true));
        AddText(tidy, candidates is null ? "Посмотрите, какие временные файлы можно удалить." :
            $"{Bytes(candidates.Sum(item => item.Bytes))} · {candidates.Length:N0} файлов", margin: new Thickness(0, 6, 0, 8));
        tidy.Children.Add(ActionButton("Открыть очистку", () => Go("Очистка")));
        var tidyCard = Card(tidy);
        tidyCard.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(tidyCard, 1);
        glance.Children.Add(tidyCard);
        Page.Children.Add(glance);
    }

    private void Planner()
    {
        var dialog = Dialog("Хватит ли места?", 390, 250);
        var body = (StackPanel)dialog.Content;
        AddText(body, "Размер установки и желаемый остаток в ГБ");
        var size = Entry("Размер установки, ГБ", "50");
        var reserve = Entry("Остаток, ГБ", "15");
        body.Children.Add(size);
        body.Children.Add(reserve);
        body.Children.Add(ActionButton("Рассчитать", () =>
        {
            if (!double.TryParse(size.Text, out var required) || !double.TryParse(reserve.Text, out var left)
                || !double.IsFinite(required + left) || required < 0 || left < 0 || required + left > 1_000_000)
            { MessageBox.Show(dialog, "Введите объёмы в ГБ.", "Расчёт"); return; }
            plannerNeed = (long)((required + left) * 1024 * 1024 * 1024);
            freeView = true;
            dialog.Close();
            Go("Обзор");
        }, true));
        dialog.ShowDialog();
    }

    private void RenderAnalysis()
    {
        Heading("Анализ диска", "Крупные файлы, распределение по папкам и размер на диске");
        var disk = Selected;
        var selector = Actions();
        foreach (var volume in volumes.Where(v => v.Ready))
            selector.Children.Add(ActionButton(volume.Title, () => OpenVolume(volume), disk?.Id == volume.Id));
        Page.Children.Add(selector);
        if (disk is null || !disk.Ready) { AddText(Page, "Этот диск недоступен. Выберите подключённый том."); return; }
        var tools = Actions();
        tools.Children.Add(ActionButton("Сканировать диск", async () =>
        {
            selectedFolder = preferredScope = null;
            await ScanVolumes([disk]);
        }, true));
        tools.Children.Add(ActionButton("Выбрать папку", async () => await PickFolder()));
        tools.Children.Add(ActionButton(showFiles ? "Папки" : "Крупные файлы", () => { showFiles = !showFiles; Render(); }));
        tools.Children.Add(ActionButton(showMap ? "Скрыть карту" : "Карта папок", () => { showMap = !showMap; Render(true); }));
        Page.Children.Add(tools);
        if (!scans.TryGetValue(disk.Id, out var scan))
        {
            var empty = new StackPanel();
            empty.Children.Add(Text("Пока нет результатов", 19, bold: true));
            AddText(empty, "Запустите сканирование выбранного диска или папки.");
            Page.Children.Add(Card(empty));
            return;
        }

        var open = selectedFolder ?? scan.Scope;
        if (!LocalStore.SamePath(open, scan.Scope) && !LocalStore.IsUnder(open, scan.Scope)) open = scan.Scope;
        var trail = Actions();
        var path = open;
        var breadcrumbs = new Stack<string>();
        while (!LocalStore.SamePath(path, scan.Scope))
        {
            breadcrumbs.Push(path);
            if (Path.GetDirectoryName(path) is not { } parent) break;
            path = parent;
        }
        breadcrumbs.Push(scan.Scope);
        foreach (var crumb in breadcrumbs)
            trail.Children.Add(ActionButton(LocalStore.SamePath(crumb, scan.Scope) ? scan.Scope : Path.GetFileName(crumb),
                () => { selectedFolder = crumb; Render(true); }));
        Page.Children.Add(trail);

        var folders = scan.Folders.Where(item => LocalStore.SamePath(Path.GetDirectoryName(item.Path), open))
            .OrderByDescending(item => item.Bytes).ToArray();
        var files = scan.AllFiles.Where(item => LocalStore.SamePath(Path.GetDirectoryName(item.Path), open))
            .OrderByDescending(item => item.Bytes).Take(3000).ToArray();
        if (showMap)
            Page.Children.Add(Card(new FolderMap(folders, file => { selectedFolder = file.Path; Render(true); }, Bytes) { Height = 225 }));
        var rows = showFiles ? scan.Rows.Where(item => LocalStore.IsUnder(item.Path, open)) :
            folders.Concat(files);
        var table = Table(rows.Select(file => new DisplayFile(file)), ("Имя", "Name", 235), ("На диске", "Size", 105),
            ("Категория", "Category", 110), ("Изменён", "Date", 96), ("Путь", "Path", 340));
        void OpenRow()
        {
            if (table.SelectedItem is not DisplayFile selected) return;
            if (selected.File.Folder) { selectedFolder = selected.File.Path; showFiles = false; Render(true); }
            else if (!demo) OpenExplorer(selected.File.Path);
        }
        table.MouseDoubleClick += (_, _) => OpenRow();
        table.KeyDown += (_, args) => { if (args.Key == Key.Enter) OpenRow(); };
        Page.Children.Add(table);
        var extras = Actions();
        extras.Children.Add(ActionButton("Открыть выбранное", OpenRow));
        extras.Children.Add(ActionButton("Скопировать путь", () => { if (table.SelectedItem is DisplayFile row) Clipboard.SetText(row.Path); }));
        extras.Children.Add(ActionButton("Найти дубликаты", async () => await FindDuplicates(scan, open)));
        Page.Children.Add(extras);
        var unassigned = LocalStore.SamePath(scan.Scope, disk.Root) && disk.Used > scan.Bytes ? $" · не распределено {Bytes(disk.Used - scan.Bytes)}" : "";
        AddText(Page, $"Учтено {Bytes(scan.Bytes)} · {scan.Files:N0} файлов · {scan.Skipped:N0} пропусков{unassigned}");
        if (scan.Truncated) AddText(Page, "Достигнут предел детализации. Просканируйте отдельную папку для полного списка.", "Warning");
    }

    private async Task PickFolder()
    {
        if (demo) { SetStatus("В демонстрационном режиме используются тестовые папки"); return; }
        var dialog = new OpenFolderDialog { Title = "Выберите локальную папку" };
        if (dialog.ShowDialog(this) != true) return;
        var target = volumes.Where(volume => volume.Ready &&
            (LocalStore.SamePath(dialog.FolderName, volume.Root) || LocalStore.IsUnder(dialog.FolderName, volume.Root)))
            .OrderByDescending(volume => volume.Root.Length).FirstOrDefault();
        if (target is null) { SetStatus("Выберите папку на доступном локальном диске"); return; }
        if ((new DirectoryInfo(dialog.FolderName).Attributes & FileAttributes.ReparsePoint) != 0)
        { SetStatus("Выберите обычную папку, а не ссылку"); return; }
        selectedVolume = target.Id;
        selectedFolder = null;
        preferredScope = dialog.FolderName;
        await ScanVolumes([target], preferredScope);
    }

    private sealed record DisplayFile(FileRow File)
    {
        public string Path => File.Path;
        public string Name => (File.Folder ? "▸  " : "") + File.Name;
        public string Size => Bytes(File.Bytes);
        public string Category => File.Category;
        public string Date => File.Changed == DateTime.MinValue ? "" : File.Changed.ToLocalTime().ToString("dd.MM.yy");
    }

    private Snapshot[] Points(string volumeId)
    {
        var entries = history.Where(s => s.VolumeId == volumeId && s.Time >= DateTimeOffset.Now.AddDays(-historyDays)).OrderBy(s => s.Time).ToArray();
        var scope = preferredScope ?? entries.LastOrDefault()?.Scope;
        return entries.Where(entry => LocalStore.SamePath(entry.Scope, scope)).ToArray();
    }

    private sealed record Difference(string Path, long Delta)
    {
        public string Size => Diff(Delta);
    }

    private static Difference[] Changes(Snapshot before, Snapshot after)
    {
        var old = before.Folders.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var next = after.Folders.ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var paths = before.Skipped == 0 && after.Skipped == 0
            ? old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase)
            : old.Keys.Intersect(next.Keys, StringComparer.OrdinalIgnoreCase);
        return paths.Select(path => new Difference(path, next.GetValueOrDefault(path) - old.GetValueOrDefault(path)))
            .Where(diff => diff.Delta != 0).OrderByDescending(diff => Math.Abs(diff.Delta)).ToArray();
    }

    private void RenderHistory()
    {
        Heading("История", "Когда менялось свободное место и какие папки выросли");
        var tools = Actions();
        foreach (var volume in volumes.Where(v => history.Any(item => item.VolumeId == v.Id)))
            tools.Children.Add(ActionButton(volume.Title, () => { selectedVolume = volume.Id; preferredScope = null; beforeChoice = 0; afterChoice = -1; Render(); },
                Selected?.Id == volume.Id));
        Page.Children.Add(tools);
        var period = Actions();
        foreach (var days in new[] { 1, 7, 30, 90 })
            period.Children.Add(ActionButton(days == 1 ? "24 часа" : $"{days} дней", () => { historyDays = days; beforeChoice = 0; afterChoice = -1; Render(true); }, historyDays == days));
        Page.Children.Add(period);
        if (Selected is not { } disk) { AddText(Page, "Дождитесь обнаружения диска."); return; }
        var points = Points(disk.Id);
        var graph = new HistoryPlot(points, Bytes) { Height = 195 };
        AutomationProperties.SetName(graph, "График свободного места по времени");
        Page.Children.Add(Card(graph));
        if (points.Length == 0) { AddText(Page, "Нет сохранённых замеров за выбранный период."); return; }
        AddText(Page, points[^1].Scope, margin: new Thickness(0, 0, 0, 8));
        var index = afterChoice < 0 ? points.Length - 1 : Math.Clamp(afterChoice, 0, points.Length - 1);
        var mark = ActionButton("Добавить метку", () =>
        {
            var name = Prompt("Название метки", "До обновления");
            if (string.IsNullOrWhiteSpace(name)) return;
            var record = points[index];
            history[history.IndexOf(record)] = record with { Mark = name };
            SaveHistory();
            Render();
        });
        if (points.Length < 2) { Page.Children.Add(mark); return; }
        int start = Math.Clamp(beforeChoice, 0, points.Length - 2);
        int end = Math.Clamp(index, start + 1, points.Length - 1);
        var labels = points.Select(p => Date(p.Time) + (string.IsNullOrWhiteSpace(p.Mark) ? "" : " · " + p.Mark)).ToArray();
        var comparison = Actions();
        var from = Choice("С начала", labels, start);
        var to = Choice("До", labels, end);
        comparison.Children.Add(from);
        comparison.Children.Add(to);
        comparison.Children.Add(mark);
        var before = points[start];
        var after = points[end];
        var changes = Changes(before, after);
        comparison.Children.Add(ActionButton("Экспорт CSV", () => Export(changes, before, after)));
        Page.Children.Add(comparison);
        from.SelectionChanged += (_, _) => { beforeChoice = from.SelectedIndex; Render(); };
        to.SelectionChanged += (_, _) => { afterChoice = to.SelectedIndex; Render(); };
        AddText(Page, $"Свободное место {Diff(after.Free - before.Free)} · между {Date(before.Time)} и {Date(after.Time)}",
            "Accent", new Thickness(0, 0, 0, 9));
        if (before.Skipped != 0 || after.Skipped != 0)
            AddText(Page, "Есть пропуски. В сравнении показаны папки, учтённые в обоих замерах.", "Warning");
        Page.Children.Add(Table(changes, ("Папка", "Path", 520), ("Изменение", "Size", 125)));
    }

    private void Export(IEnumerable<Difference> changes, Snapshot before, Snapshot after)
    {
        var file = new SaveFileDialog { Filter = "Таблица CSV|*.csv", FileName = "disk-space-comparison.csv" };
        if (file.ShowDialog(this) != true) return;
        string Quote(string value) => "\"" + (value.Length > 0 && "=+-@".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
        var lines = changes.Select(row => string.Join(",", Quote(Path.GetFileName(row.Path)),
            row.Delta.ToString(CultureInfo.InvariantCulture), before.Time.ToString("O"), after.Time.ToString("O")));
        TrySave(() => File.WriteAllLines(file.FileName, new[] { "Folder,DeltaBytes,From,To" }.Concat(lines), new UTF8Encoding(true)));
        SetStatus("Сравнение экспортировано");
    }

    private async Task FindDuplicates(ScanResult scan, string path)
    {
        var ok = await RunWork("Сравниваем содержимое файлов…", async token =>
        {
            if (demo)
            {
                duplicateRows = [new("a01c128104", @"D:\Записи\Дубль 1.mp4", 1_204_000), new("a01c128104", @"D:\Записи\Дубль 2.mp4", 1_204_000)];
                return;
            }
            duplicateRows = await Task.Run(() => Duplicates.Find(scan.AllFiles.Where(row => LocalStore.IsUnder(row.Path, path)), token), token);
        });
        if (ok) { SetStatus("Поиск дубликатов завершён"); Go("Дубликаты"); }
    }

    private void RenderDuplicates()
    {
        Heading("Дубликаты", "Файлы одинакового размера, совпавшие по SHA-256");
        if (duplicateRows is not { Length: > 0 }) { AddText(Page, "В проиндексированных файлах совпадений нет."); return; }
        var table = Table(duplicateRows, ("Группа", "Group", 106), ("Путь", "Path", 518), ("Размер, Б", "Bytes", 110));
        Page.Children.Add(table);
        Page.Children.Add(ActionButton("Открыть выбранный файл в Проводнике", () =>
        { if (!demo && table.SelectedItem is DuplicateRow row) OpenExplorer(row.Path); }));
    }

    private void RenderSearch()
    {
        Heading("Поиск", "Поиск по папкам и файлам из последнего анализа");
        var input = Entry("Имя файла или часть пути", search);
        Page.Children.Add(input);
        var table = Table(Array.Empty<DisplayFile>(), ("Имя", "Name", 240), ("На диске", "Size", 110), ("Путь", "Path", 430));
        void Filter()
        {
            search = input.Text;
            table.ItemsSource = search.Length < 2 ? [] : scans.Values.SelectMany(scan => scan.AllFiles.Concat(scan.Folders))
                .Where(row => row.Path.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(1500).Select(row => new DisplayFile(row)).ToArray();
        }
        input.TextChanged += (_, _) => Filter();
        Filter();
        Page.Children.Add(table);
        Page.Children.Add(ActionButton("Открыть в Проводнике", () => { if (!demo && table.SelectedItem is DisplayFile row) OpenExplorer(row.Path); }));
        input.Focus();
    }

    private Window Dialog(string title, int width, int height)
    {
        var body = new StackPanel { Margin = new Thickness(18) };
        return new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = width, Height = height, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = false, Background = Brush("AppBg"), Foreground = Brush("Text"), FontFamily = FontFamily, FontSize = FontSize, Content = body };
    }

    private string? Prompt(string title, string initial = "")
    {
        var dialog = Dialog(title, 430, 160);
        var panel = (StackPanel)dialog.Content;
        var input = Entry(title, initial);
        panel.Children.Add(input);
        var ok = ActionButton("Сохранить", () => dialog.DialogResult = true, true);
        ok.IsDefault = true;
        panel.Children.Add(ok);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private static TextBox Entry(string label, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 9) };
        AutomationProperties.SetName(input, label);
        return input;
    }

    private static ComboBox Choice(string label, string[] values, int index)
    {
        var combo = new ComboBox { ItemsSource = values, SelectedIndex = index, MinWidth = 130,
            MinHeight = 34, Margin = new Thickness(0, 0, 7, 5), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(combo, label);
        return combo;
    }
}
