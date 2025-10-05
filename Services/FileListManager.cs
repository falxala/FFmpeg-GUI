// Services/FileListManager.cs

using ffmpeg.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows; // Dispatcher.Invoke のために必要

namespace ffmpeg.Services
{
    public class FileListManager
    {
        public ObservableCollection<SourceFileInfo> SourceFiles { get; }
        private readonly FfmpegManager _ffmpegManager;
        private readonly string[] _supportedExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".ts", ".m2ts", ".vob", ".wav", ".mp3", ".aac", ".flac", ".ogg" };
        private static readonly SemaphoreSlim _probeSemaphore = new SemaphoreSlim(Environment.ProcessorCount);

        public FileListManager(FfmpegManager ffmpegManager)
        {
            SourceFiles = new ObservableCollection<SourceFileInfo>();
            _ffmpegManager = ffmpegManager;
        }

        public async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            var newFiles = new List<SourceFileInfo>(); // 一時リストで重複チェックを効率化
            foreach (string filePath in filePaths)
            {
                if (_supportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant()))
                {
                    var newFile = new SourceFileInfo
                    {
                        FileName = Path.GetFileName(filePath),
                        FullPath = filePath,
                        Status = "解析中..." // 解析開始前にステータスを設定
                    };

                    // ObservableCollection はUIスレッドで操作する必要がある
                    // リスト全体での重複チェックは避ける (O(N^2) になるため)
                    // まずリストに追加し、後で重複をチェックする方式にする
                    bool exists = false;
                    foreach (var existingFile in SourceFiles)
                    {
                        if (existingFile.Equals(newFile))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists && !newFiles.Contains(newFile)) // まだ追加されていない新しいファイルか
                    {
                        newFiles.Add(newFile);
                        Application.Current.Dispatcher.Invoke(() => SourceFiles.Add(newFile)); // UIスレッドで追加
                    }
                }
            }

            var probeTasks = newFiles.Select(file => ProbeAndUpdateFileInfoAsync(file)).ToList();
            await Task.WhenAll(probeTasks);
        }

        private async Task ProbeAndUpdateFileInfoAsync(SourceFileInfo fileInfo)
        {
            await _probeSemaphore.WaitAsync();
            try
            {
                var probedInfo = await _ffmpegManager.ProbeFileAsync(fileInfo.FullPath);
                if (probedInfo != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        fileInfo.Duration = probedInfo.Duration;
                        fileInfo.Container = probedInfo.Container;
                        fileInfo.VideoCodec = probedInfo.VideoCodec;
                        fileInfo.AudioCodec = probedInfo.AudioCodec;
                        fileInfo.RawDurationSeconds = probedInfo.RawDurationSeconds;
                        fileInfo.Status = "待機中"; // プロブ完了後、ステータスをリセット
                    });
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        fileInfo.Status = "プロブ失敗";
                        fileInfo.Duration = "N/A";
                        fileInfo.Container = "N/A";
                        fileInfo.VideoCodec = "N/A";
                        fileInfo.AudioCodec = "N/A";
                    });
                }
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    fileInfo.Status = $"プロブエラー: {ex.Message}";
                    fileInfo.Duration = "N/A";
                    fileInfo.Container = "N/A";
                    fileInfo.VideoCodec = "N/A";
                    fileInfo.AudioCodec = "N/A";
                });
            }
            finally
            {
                _probeSemaphore.Release();
            }
        }

        public async Task AddFolderAsync(string folderPath)
        {
            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
            await AddFilesAsync(allFiles);
        }

        public void RemoveItems(IEnumerable<SourceFileInfo> itemsToRemove)
        {
            // ObservableCollection の変更はUIスレッドで行う
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in itemsToRemove.ToList())
                {
                    SourceFiles.Remove(item);
                }
            });
        }

        public void ClearAll()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SourceFiles.Clear();
            });
        }
    }
}