// Models/SourceFileInfo.cs

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace ffmpeg.Models
{
    public class SourceFileInfo : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _fileName = string.Empty;
        private string _fullPath = string.Empty;
        private string _duration = "Probing..."; // 初期値を設定
        private string _container = "Probing...";
        private string _videoCodec = "Probing...";
        private string _audioCodec = "Probing...";
        private string _status = "待機中";
        // private double _progress = 0; // プログレスバーを削除するため、このプロパティも削除
        private double _rawDurationSeconds = 0; // 正確な秒数を格納するプロパティ

        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        // Progress プロパティは削除
        /*
        public double Progress
        {
            get => _progress;
            set => SetField(ref _progress, value);
        }
        */

        public string FileName
        {
            get => _fileName;
            set => SetField(ref _fileName, value);
        }

        public string FullPath
        {
            get => _fullPath;
            set => SetField(ref _fullPath, value);
        }

        public string Duration
        {
            get => _duration;
            set => SetField(ref _duration, value);
        }

        public string Container
        {
            get => _container;
            set => SetField(ref _container, value);
        }

        public string VideoCodec
        {
            get => _videoCodec;
            set => SetField(ref _videoCodec, value);
        }

        public string AudioCodec
        {
            get => _audioCodec;
            set => SetField(ref _audioCodec, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public double RawDurationSeconds
        {
            get => _rawDurationSeconds;
            set => SetField(ref _rawDurationSeconds, value);
        }

        public override bool Equals(object? obj)
        {
            return obj is SourceFileInfo info &&
                   FullPath.Equals(info.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return FullPath.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        // INotifyPropertyChangedの実装
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}