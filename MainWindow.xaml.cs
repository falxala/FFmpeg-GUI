// MainWindow.xaml.cs

using ffmpeg.Models;
using ffmpeg.Services;
using MaterialDesignThemes.Wpf;
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
using System.Windows.Documents;

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
        private async Task CheckAndPrepareFfmpeg()
        {
            StatusTextBlock.Text = "FFmpegの場所を確認しています...";
            SetUiEnabled(false);

            if (!_ffmpegManager.CheckSevenZaExists())
            {
                HandleError($"圧縮解除ツールが見つかりません。'Tools'フォルダに'7za.exe'を配置してください。\n期待されるパス: {_ffmpegManager.SevenZaExePath}", true);
                return;
            }

            string? ffmpegPath = _ffmpegManager.LocateFfmpeg();

            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                StatusTextBlock.Text = $"FFmpegの準備ができています。\nパス: {ffmpegPath}";
                SetUiEnabled(true);
            }
            else
            {
                // カスタムダイアログを表示
                var dialogResult = await ShowFfmpegSetupDialogAsync();

                if (dialogResult is bool b && b) // [アーカイブを選択] がクリックされた場合
                {
                    var openFileDialog = new OpenFileDialog
                    {
                        Title = "ダウンロードしたFFmpegのアーカイブファイルを選択してください",
                        Filter = "アーカイブファイル|*.zip;*.7z|すべてのファイル|*.*"
                    };

                    if (openFileDialog.ShowDialog() == true)
                    {
                        try
                        {
                            var statusProgress = new Progress<string>(status => Dispatcher.Invoke(() => StatusTextBlock.Text = status));
                            await _ffmpegManager.ExtractFfmpegArchiveAsync(openFileDialog.FileName, statusProgress);

                            ffmpegPath = _ffmpegManager.LocateFfmpeg();
                            if (!string.IsNullOrEmpty(ffmpegPath))
                            {
                                StatusTextBlock.Text = $"FFmpegの配置が完了しました！\nパス: {ffmpegPath}";
                                SetUiEnabled(true);
                            }
                            else
                            {
                                HandleError("FFmpegの展開に失敗したか、アーカイブの構造が予期されたものではありません。手動で配置してください。", true);
                            }
                        }
                        catch (Exception ex)
                        {
                            HandleError($"FFmpegの展開中にエラーが発生しました: {ex.Message}", true);
                        }
                    }
                    else
                    {
                        HandleError("ファイル選択がキャンセルされました。アプリケーションを終了します。", true);
                    }
                }
                else // ダイアログが閉じられた、または [終了] がクリックされた場合
                {
                    HandleError("FFmpegが配置されなかったため、アプリケーションを終了します。", true);
                }
            }
        }

        // ★★★ FFmpegセットアップ用のカスタムダイアログを表示するメソッドを新規追加 ★★★
        private Task<object?> ShowFfmpegSetupDialogAsync()
        {
            // ダイアログに表示するコンテンツを作成 (以前と同じ)
            var textBlock = new TextBlock { Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap };
            textBlock.Inlines.Add(new Run("FFmpegが見つかりませんでした。\nこのアプリケーションを動作させるには、FFmpegが必要です。\n\n"));
            textBlock.Inlines.Add(new Run("以下のWebサイトからFFmpegをダウンロードしてください。\n" +
                                        "(通常は 'ffmpeg-release-full.7z' や 'ffmpeg-master-latest-win64-gpl-shared.zip' などを推奨します)\n\n")
            { FontWeight = FontWeights.Bold }); textBlock.Inlines.Add(new Run("推奨ダウンロード先 (クリックで開きます):\n"));

            var gyanLink = new Hyperlink(new Run("  - Gyan.dev: https://www.gyan.dev/ffmpeg/builds/\n")) { NavigateUri = new Uri("https://www.gyan.dev/ffmpeg/builds/") };
            gyanLink.RequestNavigate += (sender, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            textBlock.Inlines.Add(gyanLink);

            var btbNLink = new Hyperlink(new Run("  - BtbN/FFmpeg-Builds: https://github.com/BtbN/FFmpeg-Builds/releases\n\n")) { NavigateUri = new Uri("https://github.com/BtbN/FFmpeg-Builds/releases") };
            btbNLink.RequestNavigate += (sender, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
            textBlock.Inlines.Add(btbNLink);

            textBlock.Inlines.Add(new Run("ダウンロード完了後、下の [アーカイブを選択] ボタンをクリックして、\nダウンロードしたアーカイブ (.7z または .zip) を選択してください。\n自動でアプリケーションフォルダ内に展開・設定します。"));

            var mainPanel = new StackPanel { Margin = new Thickness(24) };
            mainPanel.Children.Add(new TextBlock { Text = "FFmpegのセットアップが必要です", Style = (Style)FindResource("MaterialDesignHeadline6TextBlock"), Margin = new Thickness(0, 0, 0, 8) });
            mainPanel.Children.Add(textBlock);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };

            var selectButton = new Button { Content = "アーカイブを選択", Style = (Style)FindResource("MaterialDesignRaisedButton"), Command = DialogHost.CloseDialogCommand };
            selectButton.CommandParameter = true;

            var cancelButton = new Button { Content = "終了", Style = (Style)FindResource("MaterialDesignFlatButton"), Margin = new Thickness(8, 0, 0, 0), Command = DialogHost.CloseDialogCommand };
            cancelButton.CommandParameter = false;

            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(selectButton);
            mainPanel.Children.Add(buttonPanel);

            // TaskCompletionSourceを使って、イベントベースの処理をawait可能にする
            var tcs = new TaskCompletionSource<object?>();

            DialogClosingEventHandler? eventHandler = null;
            eventHandler = (sender, args) =>
            {
                // ダイアログが閉じる際に、結果をTaskCompletionSourceに設定する
                tcs.TrySetResult(args.Parameter);

                // イベントハンドラを解除してメモリリークを防ぐ
                if (eventHandler != null)
                {
                    RootDialogHost.DialogClosing -= eventHandler;
                }
            };

            // イベントを購読
            RootDialogHost.DialogClosing += eventHandler;

            // ダイアログのコンテンツを設定し、開く
            RootDialogHost.DialogContent = mainPanel;
            RootDialogHost.IsOpen = true;

            // 結果が設定されるまで待機するTaskを返す
            return tcs.Task;
        }

        /// <summary>
        /// FFmpegの準備状態に応じてUIの有効/無効を切り替えます。
        /// </summary>
        private void SetUiEnabled(bool isEnabled)
        {
            StartConversionButton.IsEnabled = isEnabled;
            SelectFileButton.IsEnabled = isEnabled;
            SelectFolderButton.IsEnabled = isEnabled;
            FileList.AllowDrop = isEnabled; // ドラッグ＆ドロップも制御
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
            // ... (以降のメソッドは変更なし)
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

                                    if (trimmedLine.StartsWith("out_time"))
                                    {
                                        HandleProgressLine(trimmedLine);
                                    }
                                    else if (trimmedLine.Contains("frame=") && trimmedLine.Contains("speed="))
                                    {
                                        FFmpegOutputLog.AppendText(trimmedLine + Environment.NewLine);
                                        FFmpegOutputLog.ScrollToEnd();
                                    }
                                    else if (trimmedLine.StartsWith("---"))
                                    {
                                        FFmpegOutputLog.AppendText(trimmedLine + Environment.NewLine);
                                        FFmpegOutputLog.ScrollToEnd();
                                    }
                                }
                            }
                        });
                    };

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

        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }
        private void SettingsOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender)
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e) { foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = true; }
        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e) { foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = false; }
        private void FileList_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) { _fileListManager.RemoveItems(_fileListManager.SourceFiles.Where(f => f.IsSelected).ToList()); }
        private void ClearListButton_Click(object sender, RoutedEventArgs e) { _fileListManager.ClearAll(); }
        private void HandleError(string message, bool shutdown = false)
        {
            StatusTextBlock.Text = $"エラー: {message}";
            MessageBox.Show(message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            if (shutdown)
            {
                Application.Current.Shutdown();
            }
        }
    }
}