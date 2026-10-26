using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pcl2Patch.Gui;

public sealed class Row
{
    public string Mark { get; set; }
    public string Title { get; set; }
    public string Detail { get; set; }
    public Brush Color { get; set; }
}

public partial class MainWindow : Window
{
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x9E, 0x5A));
    static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0xA6, 0xBA));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xD9, 0x8A, 0x1C));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xF6));
    static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(0xE7, 0xEC, 0xF3));

    readonly ObservableCollection<Row> _rows = new();
    string _path;
    bool _busy;
    int _step = 1;
    FileSnapshot _snapshot;
    List<PatchOutcome> _outcomes = new();
    PatchRun _run;

    public MainWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
        Loaded += MainWindow_Loaded;
        KeyDown += Window_KeyDown;
        GoStep(1);
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 支持把 exe 拖到快捷方式上：gui.exe "C:\...\Plain Craft Launcher 2.exe"
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(x => !x.StartsWith("-"));
        if (arg != null && File.Exists(arg)) await LoadFileAsync(arg);
    }

    // ---------------------------------------------------------------- 步骤控制
    void GoStep(int step)
    {
        _step = Math.Max(1, Math.Min(4, step));

        Panel1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Panel2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Panel3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Panel4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        PaintStep(Dot1, Num1, Lab1, 1);
        PaintStep(Dot2, Num2, Lab2, 2);
        PaintStep(Dot3, Num3, Lab3, 3);
        PaintStep(Dot4, Num4, Lab4, 4);

        Line1.Fill = _step > 1 ? Green : Pending;
        Line2.Fill = _step > 2 ? Green : Pending;
        Line3.Fill = _step > 3 ? Green : Pending;

        BtnBack.Visibility = _step >= 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnProbe.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        switch (_step)
        {
            case 1:
                BtnNext.Content = "下一步";
                BtnNext.IsEnabled = _path != null && !_busy;
                StepHint.Text = "第 1 步，共 4 步 · 选择要打补丁的启动器";
                break;
            case 2:
                var nothing = _outcomes.Count > 0 && _outcomes.All(x => x.State is PatchState.Already or PatchState.Skipped);
                BtnNext.Content = nothing ? "完成" : "下一步";
                BtnNext.IsEnabled = !_busy;
                StepHint.Text = "第 2 步，共 4 步 · 确认版本与逐项适配结果";
                break;
            case 3:
                BtnNext.Content = "开始打补丁";
                BtnNext.IsEnabled = !_busy;
                StepHint.Text = "第 3 步，共 4 步 · 确认后写入（自动备份）";
                break;
            case 4:
                BtnNext.Content = "关闭";
                BtnNext.IsEnabled = !_busy;
                StepHint.Text = "第 4 步，共 4 步 · 完成";
                break;
        }
    }

    void PaintStep(System.Windows.Controls.Border dot, System.Windows.Controls.TextBlock num,
        System.Windows.Controls.TextBlock label, int index)
    {
        if (index < _step)
        {
            dot.Background = Green;
            num.Text = "✓";
            label.Foreground = Green;
        }
        else if (index == _step)
        {
            dot.Background = Accent;
            num.Text = index.ToString();
            label.Foreground = Accent;
        }
        else
        {
            dot.Background = Pending;
            num.Text = index.ToString();
            label.Foreground = Gray;
        }
    }

    // ---------------------------------------------------------------- 文件选择
    void BtnBrowse_Click(object sender, RoutedEventArgs e) => PickFile();

    void DropArea_Click(object sender, MouseButtonEventArgs e) => PickFile();

    void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择启动器 exe",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) _ = LoadFileAsync(dlg.FileName);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var exe = files?.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (exe != null) _ = LoadFileAsync(exe);
    }

    // ---------------------------------------------------------------- 加载 + 探测
    async Task LoadFileAsync(string path)
    {
        if (_busy) return;

        _path = path;
        _outcomes = new List<PatchOutcome>();
        _run = null;
        _rows.Clear();
        LogBox.Clear();
        GoStep(2); // 先切到体检页，显示读取中

        SetBusy(true, "读取中…");
        try
        {
            var options = BuildOptions();

            var snapshot = await Task.Run(() => Patcher.Inspect(path));
            _snapshot = snapshot;
            FillVersionCard(snapshot);

            // CE 的两个开关对应 PCL2 的补丁项，CE 上用不到
            var isCe = snapshot.Kind == "CE 单文件";
            ChkAutoUpdate.IsEnabled = !isCe;
            ChkRowClick.IsEnabled = !isCe;

            var outcomes = await Task.Run(() => Patcher.Probe(path, options));
            _outcomes = outcomes;
            ShowOutcomes(outcomes);
            FillSummary(outcomes);
            Log(nothingToDo(outcomes)
                ? "该文件已处理过，无需再打补丁。"
                : $"{outcomes.Count(x => x.State == PatchState.Applied)} 项适配通过。");
        }
        catch (Exception ex)
        {
            _path = null;
            Log("读取失败: " + ex.Message);
            MessageBox.Show(this, "这个文件不是可识别的目标：\n\n" + ex.Message, "无法识别",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            GoStep(1);
            BtnNext.IsEnabled = false;
        }
        finally
        {
            SetBusy(false);
            GoStep(_step);
        }
    }

    static bool nothingToDo(IEnumerable<PatchOutcome> outcomes) =>
        outcomes.Any() && outcomes.All(x => x.State is PatchState.Already or PatchState.Skipped);

    void FillVersionCard(FileSnapshot snapshot)
    {
        var isCe = snapshot.Kind == "CE 单文件";
        KindLabel.Text = isCe ? "PCL-CE" : "PCL2";
        KindBadgeText.Text = isCe ? "社区版" : "原版";
        KindBadge.Background = isCe
            ? new SolidColorBrush(Color.FromRgb(0xEC, 0xF6, 0xEE))
            : new SolidColorBrush(Color.FromRgb(0xEC, 0xF1, 0xFE));

        VersionLabel.Text = string.IsNullOrEmpty(snapshot.Version) ? "版本 未知" : "版本 " + snapshot.Version;
        FileInfo.Text = $"{snapshot.Size:N0} 字节   ·   {snapshot.Sha256}";
    }

    void FillSummary(IReadOnlyCollection<PatchOutcome> outcomes)
    {
        var failed = outcomes.Count(x => x.State == PatchState.Failed);
        var applied = outcomes.Count(x => x.State == PatchState.Applied);

        if (failed > 0)
        {
            AdaptState.Text = "⚠ 部分未适配";
            AdaptState.Foreground = Orange;
            AdaptHint.Text = $"{failed} 项需要更新工具";
        }
        else if (nothingToDo(outcomes))
        {
            AdaptState.Text = "✅ 已是补丁状态";
            AdaptState.Foreground = Green;
            AdaptHint.Text = "无需重复处理";
        }
        else
        {
            AdaptState.Text = "✅ 已适配";
            AdaptState.Foreground = Green;
            AdaptHint.Text = $"{applied} 项可写入";
        }
    }

    void ShowOutcomes(IEnumerable<PatchOutcome> outcomes)
    {
        _rows.Clear();
        foreach (var o in outcomes)
        {
            _rows.Add(new Row
            {
                Mark = o.State switch
                {
                    PatchState.Applied => "✓",
                    PatchState.Already => "=",
                    PatchState.Skipped => "–",
                    _ => "!",
                },
                Title = o.Title,
                Detail = o.Detail,
                Color = o.State switch
                {
                    PatchState.Applied => Green,
                    PatchState.Already => Gray,
                    PatchState.Skipped => Gray,
                    _ => Red,
                },
            });
        }
        ResultsScroll.ScrollToTop();
    }

    // ---------------------------------------------------------------- 底部按钮
    async void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                if (_path != null) await LoadFileAsync(_path);
                break;
            case 2:
                if (nothingToDo(_outcomes))
                {
                    FillDone("无需处理", "这个文件已经是补丁状态。", null);
                    GoStep(4);
                    break;
                }
                FillConfirm();
                GoStep(3);
                break;
            case 3:
                await RunPatchAsync();
                break;
            case 4:
                Close();
                break;
        }
    }

    void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 4) { ResetAll(); return; }
        GoStep(_step - 1);
    }

    void BtnAgain_Click(object sender, RoutedEventArgs e) => ResetAll();

    void ResetAll()
    {
        _path = null;
        _snapshot = null;
        _outcomes = new List<PatchOutcome>();
        _run = null;
        _rows.Clear();
        LogBox.Clear();
        ChkAutoUpdate.IsEnabled = true;
        ChkRowClick.IsEnabled = true;
        GoStep(1);
        BtnNext.IsEnabled = false;
    }

    // ---------------------------------------------------------------- 第 3 步：确认 + 执行
    void FillConfirm()
    {
        var name = _snapshot == null ? "目标文件" : $"{_snapshot.AssemblyName} {_snapshot.Version}";
        TargetLabel.Text = "目标：" + _path;
        var list = _outcomes.Where(x => x.State == PatchState.Applied).Select(x => "· " + x.Title).ToList();
        ApplyList.Text = list.Count == 0
            ? "没有需要写入的项目。"
            : $"{name}  ·  将写入 {list.Count} 项\n" + string.Join("\n", list);
    }

    async Task RunPatchAsync()
    {
        if (_path == null || _busy) return;

        SetBusy(true, "处理中…");
        try
        {
            var options = BuildOptions();
            var run = await Task.Run(() => Patcher.Run(_path, null, options, true,
                m => Dispatcher.Invoke(() => Log(m))));
            _run = run;

            if (run.NothingToDo)
            {
                FillDone("无需处理", "这个文件已经是补丁状态。", null);
                GoStep(4);
                return;
            }

            if (!run.Success)
            {
                Log("失败：" + run.Message);
                MessageBox.Show(this, run.Message, "失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Log("回读自检通过，写入完成。");
            FillDone("补丁已写入",
                $"{(_snapshot == null ? "" : _snapshot.AssemblyName + " " + _snapshot.Version + "  ·  ")}写入 {run.Applied.Count} 项，回读自检通过。",
                run.BackupPath);
            GoStep(4);
        }
        catch (Exception ex)
        {
            Log("异常: " + ex.Message);
            MessageBox.Show(this, ex.Message, "异常", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            GoStep(_step);
        }
    }

    void FillDone(string title, string detail, string backup)
    {
        DoneTitle.Text = title;
        DoneDetail.Text = detail;
        BackupText.Text = backup == null ? "" : "备份：" + backup;
        DoneMark.Text = title.Contains("无需") ? "=" : "✓";
        DoneMark.Foreground = title.Contains("无需") ? Gray : Green;
    }

    // ---------------------------------------------------------------- 第 2 步：重新体检
    async void BtnProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || _busy) return;

        SetBusy(true, "体检中…");
        try
        {
            var options = BuildOptions();
            var outcomes = await Task.Run(() => Patcher.Probe(_path, options));
            _outcomes = outcomes;
            ShowOutcomes(outcomes);
            FillSummary(outcomes);
            Log($"体检完成：{outcomes.Count(x => x.State == PatchState.Applied)} 项适配通过");
        }
        catch (Exception ex)
        {
            Log("体检失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            GoStep(_step);
        }
    }

    PatchOptions BuildOptions() => new PatchOptions
    {
        KeepAutoUpdate = ChkAutoUpdate.IsChecked != true,
        NoRowClick = ChkRowClick.IsChecked != true,
    };

    // ---------------------------------------------------------------- 完成页操作
    void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _path != null ? Path.GetDirectoryName(_path) : null;
        if (dir == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void BtnCopyBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = _run?.BackupPath;
        if (string.IsNullOrEmpty(path)) { Log("没有备份路径可复制。"); return; }
        try { Clipboard.SetText(path); Log("备份路径已复制。"); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------------- 杂项
    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _step > 1 && !_busy) GoStep(_step - 1);
    }

    void SetBusy(bool busy, string text = null)
    {
        _busy = busy;
        BtnNext.IsEnabled = !busy;
        BtnProbe.IsEnabled = !busy;
        BtnBrowse.IsEnabled = !busy;
        BtnBack.IsEnabled = !busy;
        ChkAutoUpdate.IsEnabled = !busy;
        ChkRowClick.IsEnabled = !busy;
        if (busy) BtnNext.Content = text ?? "处理中…";
        else GoStep(_step);
    }

    void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\r\n");
        LogBox.ScrollToEnd();
    }
}
