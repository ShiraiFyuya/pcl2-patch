using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

    readonly ObservableCollection<Row> _rows = new();
    string _path;
    bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
        Loaded += MainWindow_Loaded;
    }

    async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 支持把 exe 拖到快捷方式上：gui.exe "C:\...\Plain Craft Launcher 2.exe"
        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(x => !x.StartsWith("-"));
        if (arg != null && File.Exists(arg)) await LoadFileAsync(arg);
    }

    // ---------------------------------------------------------------- 文件选择
    void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Plain Craft Launcher 2.exe",
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
        PathBox.Text = path;
        LogBox.Clear();
        _rows.Clear();
        EmptyHint.Visibility = Visibility.Visible;

        SetBusy(true, "读取中…");
        try
        {
            var options = new PatchOptions
            {
                KeepAutoUpdate = ChkAutoUpdate.IsChecked != true,
                NoRowClick = ChkRowClick.IsChecked != true,
            };

            var snapshot = await Task.Run(() => Patcher.Inspect(path));
            var flavor = snapshot.Kind == "CE 单文件" ? "  ·  PCL-CE" : "";
            FileInfo.Text = $"{snapshot.AssemblyName} {snapshot.Version}{flavor}   ·   {snapshot.Size:N0} 字节   ·   SHA256 {snapshot.Sha256}";

            // CE 的两个开关对应的是 PCL2 的补丁项，CE 上用不到
            var isCe = snapshot.Kind == "CE 单文件";
            ChkAutoUpdate.IsEnabled = !isCe;
            ChkRowClick.IsEnabled = !isCe;

            var outcomes = await Task.Run(() => Patcher.Probe(path, options));
            ShowOutcomes(outcomes);

            var failed = outcomes.Count(x => x.State == PatchState.Failed);
            var done = outcomes.All(x => x.State is PatchState.Already or PatchState.Skipped);
            Log(done ? "该文件已处理过，无需再打补丁。" :
                failed > 0 ? $"{failed} 项未适配 —— 本工具还没跟上这个版本。" :
                $"{outcomes.Count(x => x.State == PatchState.Applied)} 项适配通过，点「打补丁」开始。");
        }
        catch (Exception ex)
        {
            Log("读取失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
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
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ScrollToTop();
    }

    // ---------------------------------------------------------------- 探测 / 打补丁
    async void BtnProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) { Log("先选择一个 PCL 的 exe。"); return; }
        if (_busy) return;

        var options = new PatchOptions
        {
            KeepAutoUpdate = ChkAutoUpdate.IsChecked != true,
            NoRowClick = ChkRowClick.IsChecked != true,
        };

        SetBusy(true, "体检中…");
        try
        {
            var outcomes = await Task.Run(() => Patcher.Probe(path: _path, options));
            ShowOutcomes(outcomes);
            Log($"体检完成：{outcomes.Count(x => x.State == PatchState.Applied)} 项适配通过");
        }
        catch (Exception ex)
        {
            Log("体检失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void BtnPatch_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) { Log("先选择一个 PCL 的 exe。"); return; }
        if (_busy) return;

        var confirm = MessageBox.Show(this,
            "将对下面的文件原地写入补丁，并在同目录生成一次性的 .bak 备份：\n\n" + _path +
            "\n\n继续吗？", "确认打补丁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var options = new PatchOptions
        {
            KeepAutoUpdate = ChkAutoUpdate.IsChecked != true,
            NoRowClick = ChkRowClick.IsChecked != true,
        };

        SetBusy(true, "处理中…");
        try
        {
            var run = await Task.Run(() => Patcher.Run(_path, null, options, true,
                m => Dispatcher.Invoke(() => Log(m))));

            if (run.NothingToDo)
            {
                Log("无需重复打补丁。");
                return;
            }

            if (!run.Success)
            {
                Log("失败：" + run.Message);
                MessageBox.Show(this, run.Message, "失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ShowOutcomes(run.Verified);
            Log($"完成。备份：{run.BackupPath ?? "(未生成)"}");
            MessageBox.Show(this,
                "补丁已写入。\n\n" +
                "· 备份：" + (run.BackupPath ?? "无") + "\n" +
                "· 打完不要点 PCL 的「检查更新」，会覆盖成官方版",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("异常: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _path != null ? Path.GetDirectoryName(_path) : null;
        if (dir == null) { Log("先选择一个文件。"); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- 杂项
    void SetBusy(bool busy, string text = null)
    {
        _busy = busy;
        BtnPatch.IsEnabled = !busy;
        BtnProbe.IsEnabled = !busy;
        BtnBrowse.IsEnabled = !busy;
        ChkAutoUpdate.IsEnabled = !busy;
        ChkRowClick.IsEnabled = !busy;
        BtnPatch.Content = busy ? (text ?? "处理中…") : "打补丁";
    }

    void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\r\n");
        LogBox.ScrollToEnd();
    }
}
