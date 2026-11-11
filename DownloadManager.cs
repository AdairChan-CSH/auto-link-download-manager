using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkDownloader
{
    public class PageResult
    {
        public string Text { get; set; }
        public string BaseUrl { get; set; }
        public string ContentType { get; set; }
        public bool IsText { get; set; }
    }

    internal class RangesUnsupportedException : Exception
    {
        public RangesUnsupportedException() : base("The server ignored the Range request.") { }
    }

    internal class RemoteChangedException : Exception
    {
        public RemoteChangedException() : base("The remote file changed while it was being downloaded.") { }
    }

    public class DownloadManager : IDisposable
    {
        public event EventHandler Changed;
        public event Action<string> Log;

        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AllLinksDownloader/1.1";
        private const int ChunkSize = 4 * 1024 * 1024;
        private const int ReadBufferSize = 64 * 1024;

        private readonly object _gate = new object();
        private readonly List<DownloadTask> _tasks = new List<DownloadTask>();
        private readonly Queue<DownloadTask> _queue = new Queue<DownloadTask>();
        private readonly Dictionary<string, Task> _runners = new Dictionary<string, Task>();
        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly HttpClient _http;
        private readonly HttpClient _pageHttp;
        private readonly AppSettings _settings;
        private int _active;
        private bool _shutdown;

        public DownloadManager(AppSettings settings)
        {
            _settings = settings;
            _settings.Normalize();

            HttpClientHandler fileHandler = new HttpClientHandler();
            fileHandler.UseCookies = true;
            fileHandler.CookieContainer = _cookies;
            fileHandler.AutomaticDecompression = DecompressionMethods.None;
            fileHandler.MaxConnectionsPerServer = 32;
            fileHandler.AllowAutoRedirect = true;

            _http = new HttpClient(fileHandler);
            _http.Timeout = TimeSpan.FromMinutes(15);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            HttpClientHandler pageHandler = new HttpClientHandler();
            pageHandler.UseCookies = true;
            pageHandler.CookieContainer = _cookies;
            pageHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            pageHandler.MaxConnectionsPerServer = 8;
            pageHandler.AllowAutoRedirect = true;

            _pageHttp = new HttpClient(pageHandler);
            _pageHttp.Timeout = TimeSpan.FromSeconds(60);
            _pageHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _pageHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,text/plain,*/*");
        }

        public AppSettings Settings
        {
            get { return _settings; }
        }

        public List<DownloadTask> Tasks
        {
            get
            {
                lock (_gate)
                {
                    return new List<DownloadTask>(_tasks);
                }
            }
        }

        public bool HasActiveUrl(string url)
        {
            lock (_gate)
            {
                foreach (DownloadTask t in _tasks)
                {
                    if (string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase) &&
                        (t.State == TaskState.Queued || t.State == TaskState.Probing ||
                         t.State == TaskState.Downloading || t.State == TaskState.Paused ||
                         t.State == TaskState.Error))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public DownloadTask AddTask(string url, string suggestedName, string referer)
        {
            DownloadTask t;
            lock (_gate)
            {
                string name = string.IsNullOrWhiteSpace(suggestedName)
                    ? HttpUtil.InferFileName(url)
                    : suggestedName;
                name = HttpUtil.SanitizeFileName(name);
                name = MakeUniqueNameLocked(_settings.OutputFolder, name, null);

                t = new DownloadTask();
                t.Id = Guid.NewGuid().ToString("N");
                t.Url = url;
                t.Referer = referer == null ? "" : referer;
                t.FileName = name;
                t.OutputDirectory = _settings.OutputFolder;
                t.CreatedUtc = DateTime.UtcNow;
                t.StatusText = TaskState.Queued.ToString();
                t.State = TaskState.Queued;
                t.Segments = new List<SegmentState>();
                InitRuntime(t);
                _tasks.Add(t);
            }

            SaveMeta(t);
            RaiseChanged();
            LogMessage("Queued " + t.FileName);
            return t;
        }

        public void Start(DownloadTask t)
        {
            if (t == null) return;
            lock (_gate)
            {
                if (_shutdown || t.Removed) return;
                if (t.State == TaskState.Completed || t.State == TaskState.Downloading ||
                    t.State == TaskState.Probing)
                {
                    return;
                }
                if (t.State == TaskState.Queued && t.InQueue)
                {
                    return;
                }
                t.State = TaskState.Queued;
                t.LastError = null;
                if (!t.InQueue)
                {
                    t.InQueue = true;
                    _queue.Enqueue(t);
                }
                PumpLocked();
            }
            SaveMeta(t);
            RaiseChanged();
        }

        public void StartAll()
        {
            lock (_gate)
            {
                foreach (DownloadTask t in _tasks)
                {
                    if (t.Removed) continue;
                    if (t.State == TaskState.Paused || t.State == TaskState.Error ||
                        (t.State == TaskState.Queued && !t.InQueue))
                    {
                        t.State = TaskState.Queued;
                        t.LastError = null;
                        if (!t.InQueue)
                        {
                            t.InQueue = true;
                            _queue.Enqueue(t);
                        }
                    }
                }
                PumpLocked();
            }
            RaiseChanged();
        }

        public void Pause(DownloadTask t)
        {
            if (t == null) return;
            bool changed = false;
            lock (_gate)
            {
                if (t.State == TaskState.Queued)
                {
                    RemoveFromQueueLocked(t);
                    t.State = TaskState.Paused;
                    changed = true;
                }
                else if (t.State == TaskState.Downloading || t.State == TaskState.Probing)
                {
                    try
                    {
                        if (t.Cancellation != null) t.Cancellation.Cancel();
                    }
                    catch
                    {
                    }
                }
            }

            if (changed)
            {
                SaveMeta(t);
                RaiseChanged();
            }
        }

        public void PauseAll()
        {
            bool changed = false;
            lock (_gate)
            {
                foreach (DownloadTask t in _tasks)
                {
                    if (t.State == TaskState.Queued)
                    {
                        RemoveFromQueueLocked(t);
                        t.State = TaskState.Paused;
                        changed = true;
                    }
                    else if (t.State == TaskState.Downloading || t.State == TaskState.Probing)
                    {
                        try
                        {
                            if (t.Cancellation != null) t.Cancellation.Cancel();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            if (changed) RaiseChanged();
        }

        public void Resume(DownloadTask t)
        {
            Start(t);
        }

        public void ResumeAll()
        {
            StartAll();
        }

        public void Remove(DownloadTask t)
        {
            if (t == null) return;
            bool running = false;
            lock (_gate)
            {
                _tasks.Remove(t);
                RemoveFromQueueLocked(t);
                t.Removed = true;
                running = t.State == TaskState.Downloading || t.State == TaskState.Probing;
                if (running)
                {
                    try
                    {
                        if (t.Cancellation != null) t.Cancellation.Cancel();
                    }
                    catch
                    {
                    }
                }
            }

            if (!running)
            {
                CloseStream(t);
                DeletePartial(t);
                DeleteMeta(t);
            }
            RaiseChanged();
        }

        public int ClearFinished()
        {
            List<DownloadTask> finished = new List<DownloadTask>();
            lock (_gate)
            {
                foreach (DownloadTask t in _tasks)
                {
                    if (t.State == TaskState.Completed) finished.Add(t);
                }
            }
            foreach (DownloadTask t in finished)
            {
                Remove(t);
            }
            return finished.Count;
        }

        public bool TaskExists(DownloadTask t)
        {
            lock (_gate)
            {
                return _tasks.Contains(t);
            }
        }

        public void NotifySettingsChanged()
        {
            lock (_gate)
            {
                PumpLocked();
            }
        }

        public int LoadExisting()
        {
            string metaDir = Path.Combine(_settings.OutputFolder, ".alllinks");
            if (!Directory.Exists(metaDir)) return 0;

            List<DownloadTask> loaded = new List<DownloadTask>();
            string[] files;
            try
            {
                files = Directory.GetFiles(metaDir, "*.json");
            }
            catch
            {
                return 0;
            }

            foreach (string path in files)
            {
                DownloadTask t = JsonStore.Load<DownloadTask>(path);
                if (t == null || string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.FileName))
                {
                    continue;
                }

                t.OutputDirectory = _settings.OutputFolder;
                InitRuntime(t);

                long finalLength = File.Exists(t.FinalPath) ? new FileInfo(t.FinalPath).Length : -1;
                bool complete = false;
                if (t.LengthKnown && t.TotalBytes > 0 && finalLength == t.TotalBytes)
                {
                    complete = true;
                }
                else if (t.LengthKnown && t.TotalBytes == 0 && finalLength == 0)
                {
                    complete = true;
                }

                if (complete)
                {
                    t.State = TaskState.Completed;
                    t.Received = t.TotalBytes;
                    t.LastError = null;
                }
                else
                {
                    t.State = TaskState.Paused;
                    if (!t.AcceptRanges)
                    {
                        SetProgress(t, 0);
                    }
                    else
                    {
                        long partLength = File.Exists(t.TempPath) ? new FileInfo(t.TempPath).Length : 0;
                        ClampProgress(t, partLength);
                    }
                    if (!File.Exists(t.TempPath) && finalLength < 0)
                    {
                        t.LastError = "Partial data is missing.";
                    }
                }

                loaded.Add(t);
            }

            loaded.Sort(delegate (DownloadTask a, DownloadTask b)
            {
                return a.CreatedUtc.CompareTo(b.CreatedUtc);
            });

            lock (_gate)
            {
                _tasks.AddRange(loaded);
            }
            RaiseChanged();
            return loaded.Count;
        }

        public async Task<PageResult> FetchPageAsync(string url)
        {
            string normalized = url.Trim();
            if (!normalized.Contains("://"))
            {
                normalized = "https://" + normalized.TrimStart('/');
            }

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, normalized))
            {
                ApplyGlobalHeaders(req);
                using (HttpResponseMessage resp = await _pageHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    string mediaType = resp.Content.Headers.ContentType == null
                        ? ""
                        : (resp.Content.Headers.ContentType.MediaType ?? "");
                    byte[] bytes = await ReadLimitedAsync(resp.Content, 32 * 1024 * 1024);
                    string charset = resp.Content.Headers.ContentType == null
                        ? null
                        : resp.Content.Headers.ContentType.CharSet;

                    PageResult result = new PageResult();
                    result.BaseUrl = resp.RequestMessage.RequestUri.AbsoluteUri;
                    result.ContentType = mediaType;
                    result.IsText = mediaType.Length == 0 || HttpUtil.IsTextContentType(mediaType);
                    result.Text = DecodeBytes(bytes, charset);
                    return result;
                }
            }
        }

        public int CopyCompletedTo(string destination)
        {
            Directory.CreateDirectory(destination);
            List<DownloadTask> tasks = Tasks;
            int copied = 0;
            foreach (DownloadTask t in tasks)
            {
                if (t.State != TaskState.Completed || !File.Exists(t.FinalPath)) continue;
                string name = UniqueFileIn(destination, t.FileName);
                File.Copy(t.FinalPath, Path.Combine(destination, name), true);
                copied++;
            }
            return copied;
        }

        public void SaveAs(DownloadTask t, string destinationPath)
        {
            if (t == null || t.State != TaskState.Completed || !File.Exists(t.FinalPath))
            {
                throw new IOException("The file is not finished yet.");
            }
            string source = Path.GetFullPath(t.FinalPath);
            string dest = Path.GetFullPath(destinationPath);
            if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            File.Copy(source, dest, true);
        }

        public async Task ShutdownAsync()
        {
            List<Task> runners;
            List<DownloadTask> snapshot;
            lock (_gate)
            {
                _shutdown = true;
                snapshot = new List<DownloadTask>(_tasks);
                foreach (DownloadTask t in snapshot)
                {
                    if (t.State == TaskState.Queued)
                    {
                        RemoveFromQueueLocked(t);
                        t.State = TaskState.Paused;
                    }
                    else if (t.State == TaskState.Downloading || t.State == TaskState.Probing)
                    {
                        try
                        {
                            if (t.Cancellation != null) t.Cancellation.Cancel();
                        }
                        catch
                        {
                        }
                    }
                }
                runners = new List<Task>(_runners.Values);
            }

            if (runners.Count > 0)
            {
                Task all = Task.WhenAll(runners);
                await Task.WhenAny(all, Task.Delay(5000));
            }

            foreach (DownloadTask t in snapshot)
            {
                if (!t.Removed) SaveMeta(t);
            }
        }

        public void Dispose()
        {
            try { _http.Dispose(); } catch { }
            try { _pageHttp.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------- queue

        private void PumpLocked()
        {
            if (_shutdown) return;

            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Task> pair in _runners)
            {
                if (pair.Value.IsCompleted) dead.Add(pair.Key);
            }
            foreach (string id in dead) _runners.Remove(id);

            int max = Math.Max(1, _settings.ParallelFiles);
            while (_active < max && _queue.Count > 0)
            {
                DownloadTask t = _queue.Dequeue();
                t.InQueue = false;
                if (t.Removed || t.State != TaskState.Queued) continue;

                if (t.Cancellation != null) t.Cancellation.Dispose();
                t.Cancellation = new CancellationTokenSourceHolder();
                CancellationToken token = t.Cancellation.Token;

                _active++;
                Task runner = Task.Run(delegate { return RunAsync(t, token); });
                _runners[t.Id] = runner;
            }
        }

        private void RemoveFromQueueLocked(DownloadTask t)
        {
            if (!t.InQueue) return;
            t.InQueue = false;
            if (_queue.Count == 0) return;

            List<DownloadTask> keep = new List<DownloadTask>();
            while (_queue.Count > 0)
            {
                DownloadTask q = _queue.Dequeue();
                if (q != t) keep.Add(q);
            }
            foreach (DownloadTask q in keep) _queue.Enqueue(q);
        }

        // --------------------------------------------------------------- runner

        private async Task RunAsync(DownloadTask t, CancellationToken ct)
        {
            bool restarted = false;
            try
            {
                while (true)
                {
                    await PrepareAsync(t, ct);
                    if (t.State == TaskState.Completed) break;

                    t.State = TaskState.Downloading;
                    t.SawDownloading = true;
                    RaiseChanged();

                    try
                    {
                        await DownloadAllAsync(t, ct);
                        ct.ThrowIfCancellationRequested();
                        Finish(t);
                        break;
                    }
                    catch (RangesUnsupportedException)
                    {
                        CloseStream(t);
                        if (restarted) throw;
                        restarted = true;
                        LogMessage("Server ignored Range; restarting as a single stream: " + t.FileName);
                        t.AcceptRanges = false;
                        ResetAllProgress(t);
                        continue;
                    }
                    catch (RemoteChangedException)
                    {
                        CloseStream(t);
                        if (restarted) throw;
                        restarted = true;
                        LogMessage("Remote file changed; restarting: " + t.FileName);
                        t.TotalBytes = 0;
                        t.LengthKnown = false;
                        t.Segments = new List<SegmentState>();
                        t.AcceptRanges = true;
                        ResetAllProgress(t);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    t.State = TaskState.Paused;
                }
                else
                {
                    t.State = TaskState.Error;
                    t.LastError = "The request timed out.";
                }
            }
            catch (Exception ex)
            {
                t.State = TaskState.Error;
                t.LastError = FriendlyError(ex);
                LogMessage("Error on " + t.FileName + ": " + t.LastError);
            }
            finally
            {
                CloseStream(t);
                if (t.Removed)
                {
                    DeletePartial(t);
                    DeleteMeta(t);
                }
                else if (t.State == TaskState.Paused || t.State == TaskState.Error)
                {
                    SaveMeta(t);
                }

                lock (_gate)
                {
                    if (_active > 0) _active--;
                    PumpLocked();
                }
                RaiseChanged();
            }
        }

        private async Task PrepareAsync(DownloadTask t, CancellationToken ct)
        {
            Directory.CreateDirectory(t.OutputDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(t.TempPath));

            if (t.LengthKnown && t.TotalBytes > 0 && File.Exists(t.FinalPath))
            {
                long existing = new FileInfo(t.FinalPath).Length;
                if (existing == t.TotalBytes)
                {
                    CloseStream(t);
                    t.Received = t.TotalBytes;
                    t.State = TaskState.Completed;
                    return;
                }
            }

            bool hadProgress = t.SumReceived() > 0;
            if (!t.LengthKnown)
            {
                await ProbeAsync(t, ct);
            }
            else if (hadProgress && t.TotalBytes > 0)
            {
                await ValidateRemoteAsync(t, ct);
            }

            if (t.LengthKnown && t.TotalBytes == 0)
            {
                CloseStream(t);
                EnsureUniqueFinal(t);
                File.WriteAllBytes(t.FinalPath, new byte[0]);
                if (File.Exists(t.TempPath)) File.Delete(t.TempPath);
                t.Received = 0;
                t.State = TaskState.Completed;
                t.CompletedUtc = DateTime.UtcNow;
                SaveMeta(t);
                return;
            }

            if (!t.LengthKnown)
            {
                if (hadProgress)
                {
                    LogMessage("Unknown size; restarting from the beginning: " + t.FileName);
                    ResetAllProgress(t);
                }
                t.Segments = new List<SegmentState>();
                SegmentState unknown = new SegmentState();
                unknown.Start = 0;
                unknown.Length = 0;
                unknown.Received = 0;
                t.Segments.Add(unknown);
                SetProgress(t, 0);
                OpenStream(t);
                return;
            }

            EnsurePlan(t);

            if (!t.AcceptRanges && hadProgress)
            {
                LogMessage("Server cannot resume; restarting: " + t.FileName);
                ResetAllProgress(t);
            }

            long partLength = File.Exists(t.TempPath) ? new FileInfo(t.TempPath).Length : 0;
            ClampProgress(t, partLength);
            OpenStream(t);
        }

        private async Task ProbeAsync(DownloadTask t, CancellationToken ct)
        {
            t.AcceptRanges = false;

            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, t.Url))
                {
                    ApplyHeaders(req, t);
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            CaptureResponse(t, resp);
                            MaybeRenameFromHeaders(t, resp);
                            if (resp.Content.Headers.ContentLength.HasValue)
                            {
                                t.TotalBytes = resp.Content.Headers.ContentLength.Value;
                                t.LengthKnown = true;
                            }
                            if (AcceptsRanges(resp)) t.AcceptRanges = true;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            if (t.LengthKnown) return;

            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, t.Url))
                {
                    req.Headers.Range = new RangeHeaderValue(0, 0);
                    ApplyHeaders(req, t);
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (resp.StatusCode == HttpStatusCode.PartialContent)
                        {
                            t.AcceptRanges = true;
                            if (resp.Content.Headers.ContentRange != null &&
                                resp.Content.Headers.ContentRange.Length.HasValue)
                            {
                                t.TotalBytes = resp.Content.Headers.ContentRange.Length.Value;
                                t.LengthKnown = true;
                            }
                            CaptureResponse(t, resp);
                            MaybeRenameFromHeaders(t, resp);
                        }
                        else if (resp.StatusCode == HttpStatusCode.OK)
                        {
                            t.AcceptRanges = false;
                            if (resp.Content.Headers.ContentLength.HasValue)
                            {
                                t.TotalBytes = resp.Content.Headers.ContentLength.Value;
                                t.LengthKnown = true;
                            }
                            CaptureResponse(t, resp);
                            MaybeRenameFromHeaders(t, resp);
                        }
                        else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            if (resp.Content.Headers.ContentRange != null &&
                                resp.Content.Headers.ContentRange.Length.HasValue)
                            {
                                t.TotalBytes = resp.Content.Headers.ContentRange.Length.Value;
                                t.LengthKnown = true;
                            }
                        }
                        else
                        {
                            resp.EnsureSuccessStatusCode();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException("Could not inspect the URL: " + ex.Message, ex);
            }
        }

        private async Task ValidateRemoteAsync(DownloadTask t, CancellationToken ct)
        {
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, t.Url))
                {
                    ApplyHeaders(req, t);
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (!resp.IsSuccessStatusCode) return;

                        long? length = resp.Content.Headers.ContentLength;
                        if (t.LengthKnown && length.HasValue && length.Value != t.TotalBytes)
                        {
                            throw new RemoteChangedException();
                        }

                        string etag = resp.Headers.ETag == null ? null : resp.Headers.ETag.Tag;
                        if (!string.IsNullOrEmpty(t.ETag) && !string.IsNullOrEmpty(etag) &&
                            !string.Equals(t.ETag, etag, StringComparison.Ordinal))
                        {
                            throw new RemoteChangedException();
                        }

                        string modified = resp.Content.Headers.LastModified.HasValue
                            ? resp.Content.Headers.LastModified.Value.ToString("R")
                            : null;
                        if (!string.IsNullOrEmpty(t.LastModified) && !string.IsNullOrEmpty(modified) &&
                            !string.Equals(t.LastModified, modified, StringComparison.Ordinal))
                        {
                            throw new RemoteChangedException();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RemoteChangedException)
            {
                throw;
            }
            catch
            {
                // HEAD is optional; a failed validation does not stop the resume.
            }
        }

        private async Task DownloadAllAsync(DownloadTask t, CancellationToken ct)
        {
            if (!t.AcceptRanges || !t.LengthKnown || t.TotalBytes <= 0)
            {
                await DownloadSingleStreamAsync(t, ct);
                return;
            }

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                List<Task> workers = new List<Task>();
                foreach (SegmentState seg in t.Segments)
                {
                    if (seg.Length > 0 && seg.Received < seg.Length)
                    {
                        workers.Add(DownloadSegmentAsync(t, seg, linked.Token));
                    }
                }

                if (workers.Count == 0) return;

                Exception failure = null;
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (failure != null)
                {
                    try { linked.Cancel(); } catch { }
                    try { await Task.WhenAll(workers); } catch { }
                    throw failure;
                }
            }
        }

        private async Task DownloadSegmentAsync(DownloadTask t, SegmentState seg, CancellationToken ct)
        {
            int failures = 0;
            int maxRetries = Math.Max(1, _settings.Retries);

            while (seg.Received < seg.Length)
            {
                ct.ThrowIfCancellationRequested();

                long position = seg.Start + seg.Received;
                long remaining = seg.Start + seg.Length - position;
                int take = (int)Math.Min((long)ChunkSize, remaining);
                long before = seg.Received;
                int retryWait = 0;

                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, t.Url))
                    {
                        req.Headers.Range = new RangeHeaderValue(position, position + take - 1);
                        ApplyHeaders(req, t);

                        using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                if (t.LengthKnown && position >= t.TotalBytes)
                                {
                                    seg.Received = seg.Length;
                                    break;
                                }
                                throw new IOException("Server rejected the requested range.");
                            }

                            if (resp.StatusCode == HttpStatusCode.OK)
                            {
                                throw new RangesUnsupportedException();
                            }

                            if ((int)resp.StatusCode == 429 || (int)resp.StatusCode == 503)
                            {
                                throw new IOException("Server is busy (HTTP " + (int)resp.StatusCode + ").");
                            }

                            if (!resp.IsSuccessStatusCode)
                            {
                                throw new HttpRequestException("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                            }

                            ContentRangeHeaderValue range = resp.Content.Headers.ContentRange;
                            if (range != null && range.Length.HasValue && t.LengthKnown &&
                                range.Length.Value != t.TotalBytes)
                            {
                                throw new RemoteChangedException();
                            }

                            using (Stream stream = await resp.Content.ReadAsStreamAsync())
                            {
                                byte[] buffer = new byte[ReadBufferSize];
                                int chunkRead = 0;

                                while (chunkRead < take)
                                {
                                    int want = Math.Min(buffer.Length, take - chunkRead);
                                    int read = await stream.ReadAsync(buffer, 0, want, ct);
                                    if (read <= 0) break;

                                    WriteAt(t, position + chunkRead, buffer, read);
                                    chunkRead += read;
                                    seg.Received += read;
                                    Interlocked.Add(ref t.Received, read);
                                    MaybePersist(t);
                                }

                                if (chunkRead == 0)
                                {
                                    throw new IOException("The server returned no data.");
                                }
                            }
                        }
                    }

                    failures = 0;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RangesUnsupportedException)
                {
                    throw;
                }
                catch (RemoteChangedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (seg.Received > before) failures = 0;
                    failures++;
                    if (failures > maxRetries)
                    {
                        throw new IOException("Giving up after " + maxRetries + " retries: " + FriendlyError(ex), ex);
                    }
                    int wait = Math.Min(15000, 500 * failures * failures);
                    LogMessage("Retrying " + t.FileName + " in " + wait + " ms (" + FriendlyError(ex) + ")");
                    retryWait = wait;
                }

                if (retryWait > 0)
                {
                    await Task.Delay(retryWait, ct);
                }
            }
        }

        private async Task DownloadSingleStreamAsync(DownloadTask t, CancellationToken ct)
        {
            long offset = t.Received;

            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, t.Url))
            {
                ApplyHeaders(req, t);
                using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();

                    if (!t.LengthKnown && resp.Content.Headers.ContentLength.HasValue)
                    {
                        t.TotalBytes = resp.Content.Headers.ContentLength.Value;
                        t.LengthKnown = true;
                    }

                    using (Stream stream = await resp.Content.ReadAsStreamAsync())
                    {
                        byte[] buffer = new byte[128 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            WriteAt(t, offset, buffer, read);
                            offset += read;
                            Interlocked.Exchange(ref t.Received, offset);
                            if (t.Segments.Count > 0) t.Segments[0].Received = offset;
                            MaybePersist(t);
                        }
                    }

                    if (t.LengthKnown && t.TotalBytes > 0 && offset != t.TotalBytes)
                    {
                        throw new IOException("Download ended early (" + offset + " of " + t.TotalBytes + " bytes).");
                    }
                }
            }
        }

        private void Finish(DownloadTask t)
        {
            CloseStream(t);

            if (!t.LengthKnown)
            {
                t.TotalBytes = t.Received;
                t.LengthKnown = true;
            }

            if (t.TotalBytes > 0 && t.Received != t.TotalBytes)
            {
                throw new IOException("Download incomplete: " + t.Received + " of " + t.TotalBytes + " bytes.");
            }

            if (t.Segments != null && t.Segments.Count == 1)
            {
                t.Segments[0].Length = t.TotalBytes;
                t.Segments[0].Received = t.TotalBytes;
            }

            EnsureUniqueFinal(t);
            if (File.Exists(t.TempPath))
            {
                File.Move(t.TempPath, t.FinalPath);
            }

            t.Received = t.TotalBytes;
            t.State = TaskState.Completed;
            t.CompletedUtc = DateTime.UtcNow;
            t.LastError = null;
            SaveMeta(t);
            LogMessage("Saved " + t.FileName);
        }

        // --------------------------------------------------------------- helpers

        private void EnsurePlan(DownloadTask t)
        {
            bool valid = t.Segments != null && t.Segments.Count > 0;
            if (valid && t.LengthKnown && t.TotalBytes > 0)
            {
                long sum = 0;
                foreach (SegmentState seg in t.Segments)
                {
                    if (seg.Start < 0 || seg.Length <= 0) { valid = false; break; }
                    sum += seg.Length;
                }
                if (sum != t.TotalBytes) valid = false;
            }

            if (valid) return;

            t.Segments = new List<SegmentState>();
            if (!t.LengthKnown || t.TotalBytes <= 0)
            {
                SegmentState unknown = new SegmentState();
                unknown.Start = 0;
                unknown.Length = 0;
                t.Segments.Add(unknown);
                return;
            }

            int count = t.AcceptRanges ? Math.Max(1, _settings.SegmentsPerFile) : 1;
            const long minChunk = 512 * 1024;
            if (t.TotalBytes < minChunk)
            {
                count = 1;
            }
            else
            {
                long maxBySize = Math.Max(1, t.TotalBytes / minChunk);
                if (count > maxBySize) count = (int)maxBySize;
            }

            long per = t.TotalBytes / count;
            long start = 0;
            for (int i = 0; i < count; i++)
            {
                long length = (i == count - 1) ? (t.TotalBytes - start) : per;
                SegmentState seg = new SegmentState();
                seg.Start = start;
                seg.Length = length;
                seg.Received = 0;
                t.Segments.Add(seg);
                start += length;
            }
        }

        private void OpenStream(DownloadTask t)
        {
            CloseStream(t);
            FileMode mode = t.LengthKnown ? FileMode.OpenOrCreate : FileMode.Create;
            FileStream fs = new FileStream(t.TempPath, mode, FileAccess.Write,
                FileShare.Read, 64 * 1024);
            if (t.LengthKnown && t.TotalBytes > 0)
            {
                fs.SetLength(t.TotalBytes);
            }
            t.Stream = fs;
        }

        private void CloseStream(DownloadTask t)
        {
            if (t.Stream == null) return;
            try
            {
                t.Stream.Flush();
            }
            catch
            {
            }
            try
            {
                t.Stream.Dispose();
            }
            catch
            {
            }
            t.Stream = null;
        }

        private void WriteAt(DownloadTask t, long position, byte[] buffer, int count)
        {
            lock (t.StreamLock)
            {
                if (t.Stream == null) throw new IOException("The partial file is not open.");
                t.Stream.Seek(position, SeekOrigin.Begin);
                t.Stream.Write(buffer, 0, count);
            }
        }

        private void ClampProgress(DownloadTask t, long partLength)
        {
            if (t.Segments == null) return;
            foreach (SegmentState seg in t.Segments)
            {
                long available = Math.Max(0, partLength - seg.Start);
                if (seg.Received > available) seg.Received = Math.Min(seg.Length, available);
                if (seg.Received < 0) seg.Received = 0;
                if (seg.Received > seg.Length) seg.Received = seg.Length;
            }
            SetProgress(t, t.SumReceived());
        }

        private void SetProgress(DownloadTask t, long value)
        {
            Interlocked.Exchange(ref t.Received, value);
        }

        private void ResetAllProgress(DownloadTask t)
        {
            CloseStream(t);
            if (t.Segments != null)
            {
                foreach (SegmentState seg in t.Segments) seg.Received = 0;
            }
            SetProgress(t, 0);
            try
            {
                if (File.Exists(t.TempPath)) File.Delete(t.TempPath);
            }
            catch
            {
            }
        }

        private void MaybePersist(DownloadTask t)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref t.LastPersistTicks);
            double elapsed = (now - last) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < 1000.0) return;
            if (Interlocked.CompareExchange(ref t.LastPersistTicks, now, last) != last) return;
            SaveMeta(t);
        }

        private void SaveMeta(DownloadTask t)
        {
            if (t == null || t.Removed || string.IsNullOrEmpty(t.Id)) return;
            lock (t.Sync)
            {
                if (t.Removed) return;
                t.StatusText = t.State.ToString();
                JsonStore.Save(t.MetaPath, t);
            }
        }

        private void DeleteMeta(DownloadTask t)
        {
            try
            {
                if (File.Exists(t.MetaPath)) File.Delete(t.MetaPath);
            }
            catch
            {
            }
        }

        private void DeletePartial(DownloadTask t)
        {
            try
            {
                if (File.Exists(t.TempPath)) File.Delete(t.TempPath);
            }
            catch
            {
            }
        }

        private void InitRuntime(DownloadTask t)
        {
            if (t.Sync == null) t.Sync = new object();
            if (t.StreamLock == null) t.StreamLock = new object();
            if (t.Segments == null) t.Segments = new List<SegmentState>();
            if (t.ETag == null) t.ETag = "";
            if (t.LastModified == null) t.LastModified = "";
            if (t.ContentType == null) t.ContentType = "";
            if (t.LastError == null) t.LastError = "";
            if (t.Referer == null) t.Referer = "";
            if (t.CompletedUtc == DateTime.MinValue) t.CompletedUtc = t.CreatedUtc;

            t.MetaPath = Path.Combine(t.OutputDirectory, ".alllinks", t.Id + ".json");
            t.TempPath = Path.Combine(t.OutputDirectory, ".alllinks", t.Id + ".part");

            if (string.IsNullOrEmpty(t.StatusText))
            {
                t.State = TaskState.Paused;
            }
            else
            {
                try
                {
                    t.State = (TaskState)Enum.Parse(typeof(TaskState), t.StatusText, true);
                }
                catch
                {
                    t.State = TaskState.Paused;
                }
            }
            if (t.State == TaskState.Downloading || t.State == TaskState.Probing)
            {
                t.State = TaskState.Paused;
            }

            SetProgress(t, t.SumReceived());
        }

        private string MakeUniqueNameLocked(string directory, string name, DownloadTask except)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            string candidate = name;
            int counter = 1;

            while (true)
            {
                bool used = false;
                string full = Path.Combine(directory, candidate);
                if (File.Exists(full)) used = true;

                if (!used)
                {
                    foreach (DownloadTask other in _tasks)
                    {
                        if (other == except) continue;
                        if (string.Equals(other.OutputDirectory, directory, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(other.FileName, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            used = true;
                            break;
                        }
                    }
                }

                if (!used) return candidate;
                counter++;
                candidate = baseName + " (" + counter + ")" + extension;
            }
        }

        private void EnsureUniqueFinal(DownloadTask t)
        {
            lock (_gate)
            {
                t.FileName = MakeUniqueNameLocked(t.OutputDirectory, t.FileName, t);
            }
        }

        private static string UniqueFileIn(string directory, string name)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            string candidate = name;
            int counter = 1;
            while (File.Exists(Path.Combine(directory, candidate)))
            {
                counter++;
                candidate = baseName + " (" + counter + ")" + extension;
            }
            return candidate;
        }

        private void CaptureResponse(DownloadTask t, HttpResponseMessage resp)
        {
            t.ETag = resp.Headers.ETag == null ? "" : (resp.Headers.ETag.Tag ?? "");
            t.LastModified = resp.Content.Headers.LastModified.HasValue
                ? resp.Content.Headers.LastModified.Value.ToString("R")
                : "";
            t.ContentType = resp.Content.Headers.ContentType == null
                ? ""
                : (resp.Content.Headers.ContentType.MediaType ?? "");
        }

        private void MaybeRenameFromHeaders(DownloadTask t, HttpResponseMessage resp)
        {
            if (t.SumReceived() > 0) return;
            string name = FileNameFromHeaders(resp);
            if (string.IsNullOrEmpty(name)) return;

            lock (_gate)
            {
                string unique = MakeUniqueNameLocked(t.OutputDirectory, name, t);
                if (!string.Equals(unique, t.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage("Using server filename: " + unique);
                    t.FileName = unique;
                }
            }
        }

        private static string FileNameFromHeaders(HttpResponseMessage resp)
        {
            try
            {
                ContentDispositionHeaderValue cd = resp.Content.Headers.ContentDisposition;
                if (cd == null) return null;

                string name = cd.FileNameStar;
                if (string.IsNullOrEmpty(name)) name = cd.FileName;
                if (string.IsNullOrEmpty(name)) return null;

                name = name.Trim().Trim('"');
                if (name.Length > 6 && name.StartsWith("UTF-8''", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(6);
                }
                try
                {
                    name = Uri.UnescapeDataString(name);
                }
                catch
                {
                }
                return HttpUtil.SanitizeFileName(name);
            }
            catch
            {
                return null;
            }
        }

        private static bool AcceptsRanges(HttpResponseMessage resp)
        {
            if (resp.StatusCode == HttpStatusCode.PartialContent) return true;
            foreach (string value in resp.Headers.AcceptRanges)
            {
                if (value != null && value.IndexOf("bytes", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyHeaders(HttpRequestMessage req, DownloadTask t)
        {
            ApplyGlobalHeaders(req);
            if (!string.IsNullOrEmpty(t.Referer))
            {
                try
                {
                    req.Headers.Referrer = new Uri(t.Referer);
                }
                catch
                {
                }
            }
        }

        private void ApplyGlobalHeaders(HttpRequestMessage req)
        {
            List<KeyValuePair<string, string>> headers = HttpUtil.ParseExtraHeaders(_settings.ExtraHeaders);
            foreach (KeyValuePair<string, string> header in headers)
            {
                req.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes)
        {
            using (Stream stream = await content.ReadAsStreamAsync())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                int total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new IOException("The page is too large to inspect.");
                    }
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        private static string DecodeBytes(byte[] bytes, string charset)
        {
            if (bytes == null) return "";
            try
            {
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                }
                if (!string.IsNullOrEmpty(charset))
                {
                    try
                    {
                        return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
                    }
                    catch
                    {
                    }
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        private static string FriendlyError(Exception ex)
        {
            if (ex == null) return "Unknown error";
            string message = ex.Message;
            if (string.IsNullOrWhiteSpace(message) && ex.InnerException != null)
            {
                message = ex.InnerException.Message;
            }
            return message;
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        private void LogMessage(string message)
        {
            Action<string> handler = Log;
            if (handler != null)
            {
                try { handler(message); } catch { }
            }
        }
    }
}
