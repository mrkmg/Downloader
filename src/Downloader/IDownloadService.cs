﻿using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Downloader
{
    public interface IDownloadService
    {
        bool IsBusy { get; }
        long DownloadSpeed { get; }
        DownloadPackage Package { get; set; }

        Task DownloadFileAsync(DownloadPackage package);
        Task DownloadFileAsync(string address, string fileName);
        void CancelAsync();
        void Clear();

        event EventHandler<AsyncCompletedEventArgs> DownloadFileCompleted;
        event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
        event EventHandler<DownloadProgressChangedEventArgs> ChunkDownloadProgressChanged;
    }
}