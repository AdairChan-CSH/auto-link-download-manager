using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace LinkDownloader
{
    public enum TaskState
    {
        Queued,
        Probing,
        Downloading,
        Paused,
        Completed,
        Error
    }

    [DataContract]
    public class SegmentState
    {
        [DataMember] public long Start { get; set; }
        [DataMember] public long Length { get; set; }
        [DataMember] public long Received { get; set; }

        public long End
        {
            get { return Start + Length; }
        }
    }

    [DataContract]
    public class DownloadTask
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Url { get; set; }
        [DataMember] public string Referer { get; set; }
        [DataMember] public string FileName { get; set; }
        [DataMember] public string OutputDirectory { get; set; }
        [DataMember] public long TotalBytes { get; set; }
        [DataMember] public bool LengthKnown { get; set; }
        [DataMember] public bool AcceptRanges { get; set; }
        [DataMember] public string ETag { get; set; }
        [DataMember] public string LastModified { get; set; }
        [DataMember] public string ContentType { get; set; }
        [DataMember] public List<SegmentState> Segments { get; set; }
        [DataMember] public string StatusText { get; set; }
        [DataMember] public DateTime CreatedUtc { get; set; }
        [DataMember] public DateTime CompletedUtc { get; set; }
        [DataMember] public string LastError { get; set; }
        [DataMember] public bool SawDownloading { get; set; }

        [IgnoreDataMember] public TaskState State;
        [IgnoreDataMember] public object Sync;
        [IgnoreDataMember] public object StreamLock;
        [IgnoreDataMember] public FileStream Stream;
        [IgnoreDataMember] public CancellationTokenSourceHolder Cancellation;
        [IgnoreDataMember] public long Received;
        [IgnoreDataMember] public long LastPersistTicks;
        [IgnoreDataMember] public bool InQueue;
        [IgnoreDataMember] public bool Removed;
        [IgnoreDataMember] public bool DeleteOnExit;
        [IgnoreDataMember] public string MetaPath;
        [IgnoreDataMember] public string TempPath;

        public string FinalPath
        {
            get { return Path.Combine(OutputDirectory, FileName); }
        }

        public bool IsFinished
        {
            get { return State == TaskState.Completed; }
        }

        public long SumReceived()
        {
            long total = 0;
            if (Segments != null)
            {
                foreach (SegmentState s in Segments)
                {
                    total += s.Received;
                }
            }
            return total;
        }
    }

    /// <summary>Thin holder so DownloadTask stays serializable without exposing CancellationTokenSource.</summary>
    public class CancellationTokenSourceHolder
    {
        private readonly System.Threading.CancellationTokenSource _source;
        public CancellationTokenSourceHolder()
        {
            _source = new System.Threading.CancellationTokenSource();
        }
        public System.Threading.CancellationToken Token { get { return _source.Token; } }
        public void Cancel() { _source.Cancel(); }
        public void Dispose() { _source.Dispose(); }
    }
}
