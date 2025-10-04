using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf; // Ookii.Dialogs.Wpf を使用

namespace FfmpegInstallerApp
{
    // リストに表示するファイル情報を保持するクラス
    public class SourceFileInfo
    {
        // Nullable参照型エラーを回避するため、プロパティを初期化
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;

        // Equalsメソッドのシグネチャを object? に修正
        public override bool Equals(object? obj)
        {
            return obj is SourceFileInfo info &&
                   FullPath == info.FullPath;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(FullPath);
        }
    }

    public partial class MainWindow : Window
    {
        // FFmpegのダウンロードURL (Gyan's Builds の full 版は7z形式)
        private const string FFMPEG_DOWNLOAD_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z";
        // アプリケーションの実行ディレクトリからのFFmpegルートディレクトリの相対パス
        private const string FFMPEG_ROOT_DIR_RELATIVE = "ffmpeg";
        // FFmpegルートディレクトリからのffmpeg.exeの相対パス
        private const string FFMPEG_EXE_RELATIVE_PATH = "bin\\ffmpeg.exe";
        // アプリケーションの実行ディレクトリからの7za.exeの相対パス
        private const string SEVEN_ZA_EXE_RELATIVE_PATH = "Tools\\7za.exe";

        // ffmpeg.exe が存在するディレクトリのフルパス
        private string _ffmpegBinDirectory;
        // ffmpeg.exe のフルパス
        private string _ffmpegExePath;
        // 7za.exe のフルパス
        private string _sevenZaExePath;

        // 入力ファイルリスト (UIと同期)
        public ObservableCollection<SourceFileInfo> SourceFiles { get; set; }

        public MainWindow()
        {
            InitializeComponent();

            // パスを初期化
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _ffmpegBinDirectory = Path.Combine(appDirectory, FFMPEG_ROOT_DIR_RELATIVE, "bin");
            _ffmpegExePath = Path.Combine(_ffmpegBinDirectory, "ffmpeg.exe");
            _sevenZaExePath = Path.Combine(appDirectory, SEVEN_ZA_EXE_RELATIVE_PATH);

            // ObservableCollectionを初期化し、ListViewにバインド
            SourceFiles = new ObservableCollection<SourceFileInfo>();
            FileList.ItemsSource = SourceFiles;

            // アプリ起動時にFFmpegの存在チェックとダウンロードを非同期で実行
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckInitialState();
        }

        private async Task CheckInitialState()
        {
            DownloadProgressBar.Visibility = Visibility.Collapsed;

            StatusTextBlock.Text = "必要なツールの確認をしています...";

            if (!File.Exists(_sevenZaExePath))
            {
                StatusTextBlock.Text = $"エラー: 圧縮解除ツールが見つかりません。\n" +
                                       $"7-Zipのコマンドラインバージョン(7za.exe)をダウンロードし、\n" +
                                       $"アプリケーションの実行ディレクトリ内の 'Tools' フォルダに配置してください。\n" +
                                       $"期待されるパス: '{Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SEVEN_ZA_EXE_RELATIVE_PATH)}'";
                MessageBox.Show($"'{SEVEN_ZA_EXE_RELATIVE_PATH}' が見つかりません。アプリケーションを終了します。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            StatusTextBlock.Text = "FFmpegの存在を確認しています...";

            if (File.Exists(_ffmpegExePath))
            {
                StatusTextBlock.Text = $"FFmpegが見つかりました: {_ffmpegExePath}\n変換機能を使用できます。";
            }
            else
            {
                MessageBoxResult result = MessageBox.Show(
                    "FFmpegが見つかりませんでした。今すぐダウンロードして配置しますか？",
                    "FFmpegのダウンロード確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    StatusTextBlock.Text = $"FFmpegをダウンロード＆配置します...";
                    try
                    {
                        await DownloadAndExtractFfmpeg();
                        StatusTextBlock.Text = "FFmpegのダウンロードと配置が完了しました！\n変換機能を使用できます。";
                    }
                    catch (Exception ex)
                    {
                        StatusTextBlock.Text = $"FFmpegのダウンロード中にエラーが発生しました: {ex.Message}";
                        MessageBox.Show($"エラー: {ex.Message}\n詳細: {ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                        Application.Current.Shutdown();
                    }
                }
                else
                {
                    StatusTextBlock.Text = "FFmpegがダウンロードされませんでした。アプリケーションを終了します。";
                    MessageBox.Show("FFmpegがないため、アプリケーションは機能しません。", "終了", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                }
            }
        }

        private async Task DownloadAndExtractFfmpeg()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string downloadFilePath = Path.Combine(appDirectory, "ffmpeg.7z");
            string extractDirectory = Path.Combine(appDirectory, FFMPEG_ROOT_DIR_RELATIVE);

            if (Directory.Exists(extractDirectory))
            {
                StatusTextBlock.Text = $"既存のFFmpegディレクトリ ({extractDirectory}) を削除しています...";
                Directory.Delete(extractDirectory, true);
                await Task.Delay(100);
            }
            Directory.CreateDirectory(extractDirectory);

            StatusTextBlock.Text = $"FFmpegをダウンロード中: {FFMPEG_DOWNLOAD_URL}...";
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.Visibility = Visibility.Visible;

            using (HttpClient client = new HttpClient())
            {
                using (var response = await client.GetAsync(FFMPEG_DOWNLOAD_URL, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        using (var fileStream = new FileStream(downloadFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            long receivedBytes = 0;
                            int bytesRead;
                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                receivedBytes += bytesRead;
                                if (totalBytes.HasValue)
                                {
                                    double progress = (double)receivedBytes / totalBytes.Value * 100;
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DownloadProgressBar.Value = progress;
                                        StatusTextBlock.Text = $"ダウンロード中: {progress:F2}% ({receivedBytes}/{totalBytes.Value} バイト)";
                                    });
                                }
                            }
                        }
                    }
                }
            }
            DownloadProgressBar.Visibility = Visibility.Collapsed;

            StatusTextBlock.Text = "FFmpegを解凍中 (7za.exeを使用)...";

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _sevenZaExePath,
                    Arguments = $"x \"{downloadFilePath}\" -o\"{extractDirectory}\" -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    // WaitForExitAsyncを使うために、プロセスがnullでないことを確認
                    if (process == null) throw new InvalidOperationException("プロセスの開始に失敗しました。");

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    await process.WaitForExitAsync();

                    string output = await outputTask;
                    string error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"圧縮解除ツール '{SEVEN_ZA_EXE_RELATIVE_PATH}' の実行中にエラーが発生しました。\n出力:\n{output}\nエラー:\n{error}");
                    }
                    StatusTextBlock.Text += $"\n圧縮解除ツール出力:\n{output}";
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"圧縮解除ツール '{SEVEN_ZA_EXE_RELATIVE_PATH}' の実行に失敗しました。", ex);
            }

            if (File.Exists(downloadFilePath))
            {
                File.Delete(downloadFilePath);
            }

            string[] extractedSubDirs = Directory.GetDirectories(extractDirectory);
            if (extractedSubDirs.Length == 1)
            {
                string innerDir = extractedSubDirs[0];

                foreach (string entryPath in Directory.GetFileSystemEntries(innerDir))
                {
                    string entryName = Path.GetFileName(entryPath);
                    string destPath = Path.Combine(extractDirectory, entryName);

                    if (File.Exists(entryPath))
                    {
                        File.Move(entryPath, destPath);
                    }
                    else if (Directory.Exists(entryPath))
                    {
                        if (Directory.Exists(destPath))
                        {
                            Directory.Delete(destPath, true);
                        }
                        Directory.Move(entryPath, destPath);
                    }
                }
                Directory.Delete(innerDir, true);
            }

            if (File.Exists(_ffmpegExePath))
            {
                StatusTextBlock.Text += $"\nffmpeg.exe が '{_ffmpegExePath}' に見つかりました。";
            }
            else
            {
                StatusTextBlock.Text += $"\nffmpeg.exe が '{_ffmpegExePath}' に見つかりませんでした。ディレクトリ構造を確認してください。";
            }
        }

        // ======================================================
        // ファイル入力関連のメソッド
        // ======================================================

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Multiselect = true;
            openFileDialog.Filter = "動画・音声ファイル|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.mpg;*.ts;*.m2ts;*.vob;*.wav;*.mp3;*.aac;*.flac;*.ogg|すべてのファイル|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                AddFilesToList(openFileDialog.FileNames);
            }
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog();
            dialog.Description = "ファイルが含まれるフォルダを選択してください";
            dialog.UseDescriptionForTitle = true;

            if (dialog.ShowDialog(this) == true)
            {
                AddFolderToList(dialog.SelectedPath);
            }
        }

        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            // "DataRegions" を "DataFormats" に修正
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[]? droppedItems = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (droppedItems != null)
                {
                    foreach (string item in droppedItems)
                    {
                        if (File.Exists(item))
                        {
                            AddFilesToList(new string[] { item });
                        }
                        else if (Directory.Exists(item))
                        {
                            AddFolderToList(item);
                        }
                    }
                }
            }
        }

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FileList.SelectedItems.Cast<SourceFileInfo>().ToList();
            foreach (var item in selectedItems)
            {
                SourceFiles.Remove(item);
            }
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            SourceFiles.Clear();
        }



        private void AddFilesToList(IEnumerable<string> filePaths)
        {
            foreach (string filePath in filePaths)
            {
                string[] supportedExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".ts", ".m2ts", ".vob", ".wav", ".mp3", ".aac", ".flac", ".ogg" };
                if (supportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant()))
                {
                    SourceFileInfo newFile = new SourceFileInfo
                    {
                        FileName = Path.GetFileName(filePath),
                        FullPath = filePath
                    };
                    if (!SourceFiles.Any(f => f.FullPath.Equals(newFile.FullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        SourceFiles.Add(newFile);
                    }
                }
            }
        }

        private void AddFolderToList(string folderPath)
        {
            try
            {
                string[] allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                AddFilesToList(allFiles);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("アクセスが拒否されました。このフォルダのファイルを読み取ることができません。", "アクセスエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"フォルダの読み込み中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}