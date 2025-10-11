// MainWindow.xaml.cs

using ffmpeg.Models;
using ffmpeg.Services;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ffmpeg
{
    public partial class MainWindow : Window
    {
        private readonly FfmpegManager _ffmpegManager;
        private readonly FileListManager _fileListManager;
        private CancellationTokenSource? _globalConversionCts;
        private static readonly SemaphoreSlim _conversionSemaphore = new SemaphoreSlim(1);
        private int _totalFilesToConvert;
        private int _convertedFilesCount;
        private readonly object _overallProgressLock = new object();
        private SourceFileInfo? _currentlyConvertingFile;
        private readonly Regex _progressRegex = new Regex(@"out_time(?:_ms)?=(?<time>[\d:.]+)", RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();
            _ffmpegManager = new FfmpegManager();
            _fileListManager = new FileListManager(_ffmpegManager);
            FileList.ItemsSource = _fileListManager.SourceFiles;
            Loaded += MainWindow_Loaded;
            OutputFolderTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Converted");
            FFmpegOutputLog.Text = "";
            OverallProgressTextBlock.Text = "全体進捗: 0/0 (0%)";
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckAndPrepareFfmpeg();
        }

        private void StartConversionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_globalConversionCts != null && !_globalConversionCts.IsCancellationRequested)
            {
                this.Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "変換をキャンセルしています...";
                    StartConversionButton.IsEnabled = false;
                    StartConversionButton.Content = "キャンセル中...";
                });
                _globalConversionCts.Cancel();
                return;
            }
            _globalConversionCts = new CancellationTokenSource();
            Task.Run(() => StartConversion(_globalConversionCts.Token));
        }

        private async Task StartConversion(CancellationToken globalCancellationToken)
        {
            this.Dispatcher.Invoke(() => SetUiForConversion(true));
            this.Dispatcher.Invoke(() => FFmpegOutputLog.Clear());
            UpdateOverallProgress(true);

            string outputFolder = "";
            List<SourceFileInfo> filesToProcess = new List<SourceFileInfo>();
            this.Dispatcher.Invoke(() =>
            {
                outputFolder = OutputFolderTextBox.Text;
                filesToProcess = _fileListManager.SourceFiles.Where(f => f.IsSelected && f.Status != "エラー" && f.Status != "プロブ失敗").ToList();
                _totalFilesToConvert = filesToProcess.Count;
                _convertedFilesCount = 0;
                UpdateOverallProgress();
                foreach (var fileInfo in _fileListManager.SourceFiles)
                {
                    if (fileInfo.IsSelected && fileInfo.Status != "エラー" && fileInfo.Status != "プロブ失敗")
                    {
                        fileInfo.Status = "待機中";
                    }
                    else if (!fileInfo.IsSelected)
                    {
                        fileInfo.Status = "選択解除";
                    }
                }
            });

            if (string.IsNullOrWhiteSpace(outputFolder) || !filesToProcess.Any())
            {
                this.Dispatcher.Invoke(() => MessageBox.Show("変換するファイルを選択し、出力先を指定してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information));
                this.Dispatcher.Invoke(() => SetUiForConversion(false));
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                try { Directory.CreateDirectory(outputFolder); }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() => MessageBox.Show($"出力フォルダの作成に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error));
                    this.Dispatcher.Invoke(() => SetUiForConversion(false));
                    return;
                }
            }

            foreach (var fileInfo in filesToProcess)
            {
                if (globalCancellationToken.IsCancellationRequested)
                {
                    this.Dispatcher.Invoke(() => { fileInfo.Status = "キャンセル"; });
                    continue;
                }
                await _conversionSemaphore.WaitAsync(globalCancellationToken);
                try
                {
                    _currentlyConvertingFile = fileInfo;
                    string originalFileName = Path.GetFileNameWithoutExtension(fileInfo.FileName);
                    string extension = ".mp4";
                    string outputPathForFile = Path.Combine(outputFolder, $"{originalFileName}{extension}");
                    int counter = 1;
                    while (File.Exists(outputPathForFile))
                    {
                        outputPathForFile = Path.Combine(outputFolder, $"{originalFileName} ({counter++}){extension}");
                    }
                    this.Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = "変換を開始しました";
                        fileInfo.Status = "変換中...";
                    });

                    // ★★★ ここからが修正されたログ処理コールバック ★★★
                    Action<string> outputLogCallback = (log) =>
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            using (var reader = new StringReader(log))
                            {
                                string? line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;

                                    string trimmedLine = line.Trim();

                                    // 1. 内部処理用の進捗行か？
                                    if (trimmedLine.StartsWith("out_time"))
                                    {
                                        HandleProgressLine(trimmedLine);
                                    }
                                    // 2. 表示したい進捗行か？
                                    else if (trimmedLine.Contains("frame=") && trimmedLine.Contains("speed="))
                                    {
                                        FFmpegOutputLog.AppendText(trimmedLine + Environment.NewLine);
                                        FFmpegOutputLog.ScrollToEnd();
                                    }
                                    // 3. 表示したい開始/終了行か？
                                    else if (trimmedLine.StartsWith("---"))
                                    {
                                        FFmpegOutputLog.AppendText(trimmedLine + Environment.NewLine);
                                        FFmpegOutputLog.ScrollToEnd();
                                    }
                                    // 4. 上記以外は無視
                                }
                            }
                        });
                    };
                    // ★★★ ここまで ★★★

                    await _ffmpegManager.ConvertFileAsync(fileInfo, outputPathForFile, outputLogCallback, globalCancellationToken);

                    if (!globalCancellationToken.IsCancellationRequested)
                    {
                        this.Dispatcher.Invoke(() => { fileInfo.Status = "完了"; });
                        lock (_overallProgressLock) { _convertedFilesCount++; UpdateOverallProgress(); }
                    }
                }
                catch (OperationCanceledException)
                {
                    this.Dispatcher.Invoke(() => { fileInfo.Status = "キャンセル"; });
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() =>
                    {
                        fileInfo.Status = "エラー";
                        FFmpegOutputLog.AppendText($"[{fileInfo.FileName}] 変換中にエラーが発生しました: {ex.Message}{Environment.NewLine}");
                        FFmpegOutputLog.ScrollToEnd();
                        var result = MessageBox.Show($"{fileInfo.FileName} の変換中にエラーが発生しました。\n\n詳細: {ex.Message}\n\n変換を続行しますか？", "変換エラー", MessageBoxButton.YesNo, MessageBoxImage.Error);
                        if (result == MessageBoxResult.No) { _globalConversionCts?.Cancel(); }
                    });
                }
                finally
                {
                    _currentlyConvertingFile = null;
                    _conversionSemaphore.Release();
                }
            }
            this.Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = globalCancellationToken.IsCancellationRequested ? "変換がキャンセルされました" : "すべての変換が完了しました";
            });
            this.Dispatcher.Invoke(() => SetUiForConversion(false));
            _globalConversionCts?.Dispose();
            _globalConversionCts = null;
        }

        private void HandleProgressLine(string line)
        {
            if (_currentlyConvertingFile == null || _currentlyConvertingFile.RawDurationSeconds <= 0)
            {
                return;
            }
            Match match = _progressRegex.Match(line);
            if (match.Success)
            {
                string timeString = match.Groups["time"].Value;
                double currentSeconds;
                if (long.TryParse(timeString, out long microSeconds))
                {
                    currentSeconds = microSeconds / 1_000_000.0;
                }
                else if (TimeSpan.TryParse(timeString, CultureInfo.InvariantCulture, out TimeSpan currentTime))
                {
                    currentSeconds = currentTime.TotalSeconds;
                }
                else
                {
                    return;
                }
                double totalSeconds = _currentlyConvertingFile.RawDurationSeconds;
                double calculatedProgress = (currentSeconds / totalSeconds) * 100;
                CurrentFileProgressBar.Value = calculatedProgress;
                CurrentFileProgressTextBlock.Text = $"{calculatedProgress:F1}%";
            }
        }

        private void UpdateOverallProgress(bool reset = false)
        {
            this.Dispatcher.Invoke(() =>
            {
                if (reset) { _convertedFilesCount = 0; _totalFilesToConvert = 0; }
                if (_totalFilesToConvert > 0)
                {
                    double percentage = (double)_convertedFilesCount / _totalFilesToConvert * 100;
                    OverallProgressTextBlock.Text = $"全体進捗: {_convertedFilesCount}/{_totalFilesToConvert} ({percentage:F1}%)";
                }
                else { OverallProgressTextBlock.Text = "全体進捗: 0/0 (0%)"; }
            });
        }

        private void SetUiForConversion(bool isConverting)
        {
            SelectFileButton.IsEnabled = !isConverting; SelectFolderButton.IsEnabled = !isConverting;
            FileList.IsEnabled = !isConverting; RemoveSelectedButton.IsEnabled = !isConverting;
            ClearListButton.IsEnabled = !isConverting; SelectOutputFolderButton.IsEnabled = !isConverting;
            OutputFolderTextBox.IsEnabled = !isConverting;
            StartConversionButton.Content = isConverting ? "変換中止" : "変換開始";
            StartConversionButton.IsEnabled = true;
            if (isConverting)
            {
                CurrentFileProgressGrid.Visibility = Visibility.Visible;
                CurrentFileProgressBar.Value = 0;
                CurrentFileProgressTextBlock.Text = "0.0%";
            }
            else
            {
                CurrentFileProgressGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog { Multiselect = true, Filter = "メディアファイル|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.mpg;*.ts;*.m2ts;*.vob;*.wav;*.mp3;*.aac;*.flac;*.ogg|すべてのファイル|*.*" };
            if (openFileDialog.ShowDialog() == true) { Task.Run(() => _fileListManager.AddFilesAsync(openFileDialog.FileNames)); }
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog { Description = "フォルダを選択してください", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == true) { Task.Run(() => _fileListManager.AddFolderAsync(dialog.SelectedPath)); }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedItems)
            {
                Task.Run(async () =>
                {
                    foreach (string item in droppedItems)
                    {
                        if (File.Exists(item)) await _fileListManager.AddFilesAsync(new[] { item });
                        else if (Directory.Exists(item)) await _fileListManager.AddFolderAsync(item);
                    }
                });
            }
        }

        private void SelectOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog { Description = "出力先フォルダを選択してください", UseDescriptionForTitle = true, SelectedPath = OutputFolderTextBox.Text };
            if (dialog.ShowDialog(this) == true) { OutputFolderTextBox.Text = dialog.SelectedPath; }
        }

        private async Task CheckAndPrepareFfmpeg()
        {
            StatusTextBlock.Text = "必要なツールの確認をしています...";
            if (!_ffmpegManager.CheckSevenZaExists()) { HandleError($"圧縮解除ツールが見つかりません。'Tools'フォルダに'7za.exe'を配置してください。\n期待されるパス: {_ffmpegManager.SevenZaExePath}", true); return; }
            if (_ffmpegManager.CheckFfmpegExists()) { StatusTextBlock.Text = $"FFmpegの準備ができています。\nパス: {_ffmpegManager.FfmpegExePath}"; }
            else
            {
                var result = MessageBox.Show("FFmpegが見つかりませんでした。今すぐダウンロードして配置しますか？", "FFmpegのダウンロード確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        var statusProgress = new Progress<string>(status => this.Dispatcher.Invoke(() => StatusTextBlock.Text = status));
                        var downloadProgress = new Progress<double>(progress => { this.Dispatcher.Invoke(() => { StatusTextBlock.Text = $"FFmpegダウンロード中: {progress:F1}%"; }); });
                        await _ffmpegManager.DownloadAndExtractFfmpegAsync(statusProgress, downloadProgress);
                        StatusTextBlock.Text = "FFmpegのダウンロードと配置が完了しました！";
                    }
                    catch (Exception ex) { HandleError($"FFmpegのダウンロード中にエラーが発生しました: {ex.Message}", true); }
                }
                else { HandleError("FFmpegがないため、アプリケーションは機能しません。", true); }
            }
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e) { foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = true; }
        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e) { foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = false; }
        private void FileList_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) { _fileListManager.RemoveItems(_fileListManager.SourceFiles.Where(f => f.IsSelected).ToList()); }
        private void ClearListButton_Click(object sender, RoutedEventArgs e) { _fileListManager.ClearAll(); }
        private void HandleError(string message, bool shutdown = false) { StatusTextBlock.Text = $"エラー: {message}"; MessageBox.Show(message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error); if (shutdown) Application.Current.Shutdown(); }
    }
}