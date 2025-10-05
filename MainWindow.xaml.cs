// MainWindow.xaml.cs

using ffmpeg.Models;
using ffmpeg.Services;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text; // StringBuilder のために必要
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ffmpeg
{
    public partial class MainWindow : Window
    {
        private readonly FfmpegManager _ffmpegManager;
        private readonly FileListManager _fileListManager;
        private CancellationTokenSource? _globalConversionCts; // 全体キャンセルトークン

        // 同時実行数を制限するためのSemaphoreSlim
        private static readonly SemaphoreSlim _conversionSemaphore = new SemaphoreSlim(Environment.ProcessorCount > 1 ? Environment.ProcessorCount - 1 : 1); // CPUコア数-1、最小1

        public MainWindow()
        {
            InitializeComponent();
            _ffmpegManager = new FfmpegManager();
            _fileListManager = new FileListManager(_ffmpegManager);
            FileList.ItemsSource = _fileListManager.SourceFiles;

            Loaded += MainWindow_Loaded;
            OutputFolderTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Converted");
            FFmpegOutputLog.Text = ""; // ログTextBoxを初期化
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
            this.Dispatcher.Invoke(() => FFmpegOutputLog.Clear()); // 新しい変換セッションごとにログをクリア

            string outputFolder = "";
            List<SourceFileInfo> filesToProcess = new List<SourceFileInfo>();

            this.Dispatcher.Invoke(() =>
            {
                outputFolder = OutputFolderTextBox.Text;
                filesToProcess = _fileListManager.SourceFiles
                                    .Where(f => f.IsSelected && f.Status != "エラー" && f.Status != "プロブ失敗")
                                    .ToList();

                // 各ファイルのStatusをリセット
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

                StatusTextBlock.Text = $"{filesToProcess.Count}個のファイルの変換を開始します...";
            });

            if (string.IsNullOrWhiteSpace(outputFolder) || !filesToProcess.Any())
            {
                this.Dispatcher.Invoke(() => MessageBox.Show("変換するファイルを選択し、出力先を指定してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information));
                this.Dispatcher.Invoke(() => SetUiForConversion(false));
                return;
            }

            if (!Directory.Exists(outputFolder))
            {
                try
                {
                    Directory.CreateDirectory(outputFolder);
                }
                catch (Exception ex)
                {
                    this.Dispatcher.Invoke(() => MessageBox.Show($"出力フォルダの作成に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error));
                    this.Dispatcher.Invoke(() => SetUiForConversion(false));
                    return;
                }
            }

            int completedCount = 0;
            var runningTasks = new List<Task>();

            foreach (var fileInfo in filesToProcess)
            {
                if (globalCancellationToken.IsCancellationRequested)
                {
                    this.Dispatcher.Invoke(() => { fileInfo.Status = "キャンセル"; });
                    continue;
                }

                // FFmpeg生出力をログTextBoxに追記するためのコールバック
                Action<string> outputLogCallback = (log) =>
                {
                    // Dispatcher.Invoke を使用してUIスレッドでログを追記
                    this.Dispatcher.Invoke(() =>
                    {
                        FFmpegOutputLog.AppendText(log);
                        FFmpegOutputLog.ScrollToEnd(); // 自動スクロール
                    });
                };

                await _conversionSemaphore.WaitAsync(globalCancellationToken);
                runningTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string originalFileName = Path.GetFileNameWithoutExtension(fileInfo.FileName);
                        string extension = ".mp4";
                        string outputPathForFile = Path.Combine(outputFolder, $"{originalFileName}{extension}");
                        int counter = 1;
                        while (File.Exists(outputPathForFile))
                        {
                            outputPathForFile = Path.Combine(outputFolder, $"{originalFileName} ({counter++}){extension}");
                        }

                        this.Dispatcher.Invoke(() => { fileInfo.Status = "変換中..."; });

                        await _ffmpegManager.ConvertFileAsync(fileInfo, outputPathForFile, outputLogCallback, globalCancellationToken);

                        if (!globalCancellationToken.IsCancellationRequested)
                        {
                            this.Dispatcher.Invoke(() => { fileInfo.Status = "完了"; });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        this.Dispatcher.Invoke(() => { fileInfo.Status = "キャンセル"; });
                        outputLogCallback($"[{fileInfo.FileName}] 変換がキャンセルされました。{Environment.NewLine}");
                    }
                    catch (Exception ex)
                    {
                        this.Dispatcher.Invoke(() =>
                        {
                            fileInfo.Status = "エラー";
                            outputLogCallback($"[{fileInfo.FileName}] 変換中にエラーが発生しました: {ex.Message}{Environment.NewLine}");

                            var result = MessageBox.Show(
                                $"{fileInfo.FileName} の変換中にエラーが発生しました。\n\n詳細: {ex.Message}\n\n変換を続行しますか？",
                                "変換エラー", MessageBoxButton.YesNo, MessageBoxImage.Error);
                            if (result == MessageBoxResult.No)
                            {
                                // 全体キャンセルをトリガー
                                globalCancellationToken.ThrowIfCancellationRequested();
                            }
                        });
                    }
                    finally
                    {
                        _conversionSemaphore.Release();
                        this.Dispatcher.Invoke(() => { /* 何もしない */ }); // UI更新のため、Invokeを介してCompletedCountを更新
                        Interlocked.Increment(ref completedCount); // 完了したタスク数をスレッドセーフにインクリメント
                        this.Dispatcher.Invoke(() =>
                        {
                            StatusTextBlock.Text = $"変換中: {completedCount}/{filesToProcess.Count} 個完了..."; // 全体進捗をステータステキストで表現
                        });
                    }
                }, globalCancellationToken));
            }

            try
            {
                await Task.WhenAll(runningTasks);
            }
            catch (OperationCanceledException)
            {
                // グローバルキャンセルがタスク内に伝播してTask.WhenAllがキャンセルされた場合
            }

            this.Dispatcher.Invoke(() =>
            {
                if (globalCancellationToken.IsCancellationRequested)
                {
                    StatusTextBlock.Text = "変換がキャンセルされました。";
                }
                else
                {
                    int actualCompletedCount = _fileListManager.SourceFiles.Count(f => f.IsSelected && f.Status == "完了");
                    if (actualCompletedCount == filesToProcess.Count)
                    {
                        StatusTextBlock.Text = "すべての変換が完了しました。";
                    }
                    else
                    {
                        StatusTextBlock.Text = $"変換が終了しました。{actualCompletedCount}/{filesToProcess.Count} 個完了 (一部エラーまたはキャンセル)。";
                    }
                }
            });

            this.Dispatcher.Invoke(() => SetUiForConversion(false));
            _globalConversionCts?.Dispose();
            _globalConversionCts = null;
        }

        // --- UIの状態を切り替えるメソッド ---
        private void SetUiForConversion(bool isConverting)
        {
            SelectFileButton.IsEnabled = !isConverting;
            SelectFolderButton.IsEnabled = !isConverting;
            FileList.IsEnabled = !isConverting;
            RemoveSelectedButton.IsEnabled = !isConverting;
            ClearListButton.IsEnabled = !isConverting;
            SelectOutputFolderButton.IsEnabled = !isConverting;
            OutputFolderTextBox.IsEnabled = !isConverting;

            StartConversionButton.Content = isConverting ? "変換中止" : "変換開始";
            StartConversionButton.IsEnabled = true;
            // プログレスバーは削除されたため、ここでの処理は不要
        }

        // --- ファイル入力イベントハンドラ ---
        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog { Multiselect = true, Filter = "メディアファイル|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.mpg;*.ts;*.m2ts;*.vob;*.wav;*.mp3;*.aac;*.flac;*.ogg|すべてのファイル|*.*" };
            if (openFileDialog.ShowDialog() == true)
            {
                Task.Run(() => _fileListManager.AddFilesAsync(openFileDialog.FileNames));
            }
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog { Description = "フォルダを選択してください", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == true)
            {
                Task.Run(() => _fileListManager.AddFolderAsync(dialog.SelectedPath));
            }
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
            if (dialog.ShowDialog(this) == true)
            {
                OutputFolderTextBox.Text = dialog.SelectedPath;
            }
        }

        private async Task CheckAndPrepareFfmpeg()
        {
            // プログレスバーは削除されたため、Visibility設定は不要
            StatusTextBlock.Text = "必要なツールの確認をしています...";

            if (!_ffmpegManager.CheckSevenZaExists())
            {
                HandleError($"圧縮解除ツールが見つかりません。'Tools'フォルダに'7za.exe'を配置してください。\n期待されるパス: {_ffmpegManager.SevenZaExePath}", true);
                return;
            }

            if (_ffmpegManager.CheckFfmpegExists())
            {
                StatusTextBlock.Text = $"FFmpegの準備ができています。\nパス: {_ffmpegManager.FfmpegExePath}";
            }
            else
            {
                var result = MessageBox.Show("FFmpegが見つかりませんでした。今すぐダウンロードして配置しますか？", "FFmpegのダウンロード確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        var statusProgress = new Progress<string>(status => this.Dispatcher.Invoke(() => StatusTextBlock.Text = status));
                        var downloadProgress = new Progress<double>(progress =>
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                // ダウンロード進捗をStatusTextBlockで表示（プログレスバーはないため）
                                StatusTextBlock.Text = $"FFmpegダウンロード中: {progress:F1}%";
                            });
                        });
                        // プログレスバーは削除されたため、Visible設定は不要
                        await _ffmpegManager.DownloadAndExtractFfmpegAsync(statusProgress, downloadProgress);
                        StatusTextBlock.Text = "FFmpegのダウンロードと配置が完了しました！";
                    }
                    catch (Exception ex)
                    {
                        HandleError($"FFmpegのダウンロード中にエラーが発生しました: {ex.Message}", true);
                    }
                }
                else
                {
                    HandleError("FFmpegがないため、アプリケーションは機能しません。", true);
                }
            }
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = true;
        }
        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (var fileInfo in _fileListManager.SourceFiles) fileInfo.IsSelected = false;
        }
        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            _fileListManager.RemoveItems(_fileListManager.SourceFiles.Where(f => f.IsSelected).ToList());
        }
        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            _fileListManager.ClearAll();
        }
        private void HandleError(string message, bool shutdown = false)
        {
            StatusTextBlock.Text = $"エラー: {message}";
            MessageBox.Show(message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            if (shutdown) Application.Current.Shutdown();
        }
    }
}