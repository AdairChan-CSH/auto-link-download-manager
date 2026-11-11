using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkDownloader
{
    internal static class Tests
    {
        private static int _failures;

        private static int Main()
        {
            try
            {
                TestLinkExtraction();
                TestDownloadAndResumeAsync().GetAwaiter().GetResult();
                TestSingleStreamAsync().GetAwaiter().GetResult();
                TestUnknownLengthStalePartialAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: unexpected exception");
                Console.WriteLine(ex);
                return 1;
            }

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            Console.WriteLine(_failures + " TEST(S) FAILED");
            return 1;
        }

        // ------------------------------------------------------------- extraction

        private static void TestLinkExtraction()
        {
            Console.WriteLine("Test: link extraction");
            string html =
                "<html><head><base href=\"https://example.com/docs/\"></head><body>" +
                "<a href=\"a.html\">A</a>" +
                "<a href='/files/b.zip?x=1&amp;y=2'>B</a>" +
                "<img src=\"../img/pic.png\">" +
                "<a href=\"mailto:x@y.z\">mail</a>" +
                "<a href=\"#top\">top</a>" +
                "<a href=\"javascript:void(0)\">js</a>" +
                "<a href=\"https://example.com/docs/a.html#frag\">dup</a>" +
                "https://other.test/video.mp4" +
                "</body></html>";

            int skipped;
            List<LinkItem> links = LinkExtractor.Extract(html, "https://example.com/page.html", out skipped);

            Assert(links.Count == 4, "Expected 4 links, got " + links.Count);
            Assert(Find(links, "example.com/docs/a.html") != null, "Base-resolved relative link missing");
            LinkItem archive = Find(links, "files/b.zip");
            Assert(archive != null, "Quoted relative link missing");
            if (archive != null)
            {
                Assert(archive.FileName == "b.zip", "Expected b.zip, got " + archive.FileName);
                Assert(archive.Category == "Archive", "Expected Archive category, got " + archive.Category);
                Assert(archive.Url.IndexOf("x=1", StringComparison.Ordinal) > 0, "Query string lost");
            }
            LinkItem image = Find(links, "img/pic.png");
            Assert(image != null, "Parent-relative image missing");
            LinkItem video = Find(links, "other.test/video.mp4");
            Assert(video != null, "Absolute URL in body missing");
            if (video != null)
            {
                Assert(video.Category == "Video", "Expected Video category");
            }
            Assert(skipped == 3, "Expected 3 skipped entries, got " + skipped);

            int skipped2;
            List<LinkItem> plain = LinkExtractor.Extract(
                "https://a.test/x.bin\nnot a link\nhttps://b.test/y.zip", null, out skipped2);
            Assert(plain.Count == 2, "Plain URL list: expected 2, got " + plain.Count);
            Console.WriteLine("  PASS (" + links.Count + " links, " + skipped + " skipped)");
        }

        private static LinkItem Find(List<LinkItem> links, string fragment)
        {
            foreach (LinkItem item in links)
            {
                if (item.Url.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return item;
            }
            return null;
        }

        // -------------------------------------------------------------- downloads

        private static async Task TestDownloadAndResumeAsync()
        {
            Console.WriteLine("Test: segmented download, pause, restart, continue");
            byte[] payload = MakePayload(6 * 1024 * 1024);
            string expected = Hash(payload);
            string root = Path.Combine(Path.GetTempPath(), "alllinks-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            using (TestServer server = new TestServer(payload, true, 8))
            {
                AppSettings settings = NewSettings(root);
                DownloadTask task;

                try
                {
                    using (DownloadManager first = new DownloadManager(settings))
                    {
                        task = first.AddTask(server.BaseUrl + "/file.bin", "file.bin", "");
                        first.Start(task);

                        bool progressed = WaitFor(delegate
                        {
                            return Interlocked.Read(ref task.Received) > 200 * 1024;
                        }, 15000);
                        Assert(progressed, "Download did not make progress. State=" + task.State +
                            " Received=" + Interlocked.Read(ref task.Received));

                        first.Pause(task);
                        bool paused = WaitFor(delegate
                        {
                            return task.State == TaskState.Paused;
                        }, 15000);
                        Assert(paused, "Pause did not settle. State=" + task.State);

                        long pausedBytes = Interlocked.Read(ref task.Received);
                        Assert(pausedBytes > 0, "No bytes were saved before pause");
                        Console.WriteLine("  Paused at " + pausedBytes + " bytes of " + task.TotalBytes);

                        await first.ShutdownAsync();
                        Assert(File.Exists(task.MetaPath), "Resume metadata was not written");
                        Assert(File.Exists(task.TempPath), "Partial file was not kept");
                    }

                    using (DownloadManager second = new DownloadManager(settings))
                    {
                        int loaded = second.LoadExisting();
                        Assert(loaded == 1, "Expected 1 task after restart, got " + loaded);

                        List<DownloadTask> tasks = second.Tasks;
                        DownloadTask resumed = tasks[0];
                        Assert(resumed.State == TaskState.Paused, "Loaded task should be paused, got " + resumed.State);
                        long before = Interlocked.Read(ref resumed.Received);
                        Assert(before > 0, "Saved progress was lost on reload");
                        Console.WriteLine("  Reloaded with " + before + " bytes");

                        second.Start(resumed);
                        bool finished = WaitFor(delegate
                        {
                            return resumed.State == TaskState.Completed || resumed.State == TaskState.Error;
                        }, 60000);

                        Assert(finished, "Resume timed out. State=" + resumed.State);
                        Assert(resumed.State == TaskState.Completed,
                            "Resume failed: " + resumed.LastError);
                        Assert(File.Exists(resumed.FinalPath), "Final file was not created");
                        if (File.Exists(resumed.FinalPath))
                        {
                            Assert(Hash(File.ReadAllBytes(resumed.FinalPath)) == expected,
                                "Downloaded bytes do not match the source");
                            Assert(new FileInfo(resumed.FinalPath).Length == payload.Length,
                                "Final file has the wrong length");
                        }
                        Console.WriteLine("  PASS (resumed to completion, hash matches)");
                    }
                }
                finally
                {
                    TryDelete(root);
                }
            }
        }

        private static async Task TestSingleStreamAsync()
        {
            Console.WriteLine("Test: server without range support");
            byte[] payload = MakePayload(1500 * 1024);
            string expected = Hash(payload);
            string root = Path.Combine(Path.GetTempPath(), "alllinks-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            using (TestServer server = new TestServer(payload, false, 2))
            {
                AppSettings settings = NewSettings(root);
                try
                {
                    using (DownloadManager manager = new DownloadManager(settings))
                    {
                        DownloadTask task = manager.AddTask(server.BaseUrl + "/file.bin", "plain.bin", "");
                        manager.Start(task);

                        bool finished = WaitFor(delegate
                        {
                            return task.State == TaskState.Completed || task.State == TaskState.Error;
                        }, 30000);

                        Assert(finished, "Single-stream download timed out");
                        Assert(task.State == TaskState.Completed, "Single-stream failed: " + task.LastError);
                        Assert(!task.AcceptRanges, "Range support should be false");
                        if (File.Exists(task.FinalPath))
                        {
                            Assert(Hash(File.ReadAllBytes(task.FinalPath)) == expected,
                                "Single-stream bytes do not match");
                        }
                        else
                        {
                            Assert(false, "Single-stream final file missing");
                        }
                    }
                }
                finally
                {
                    TryDelete(root);
                }
            }
            Console.WriteLine("  PASS");
            await Task.FromResult(0);
        }

        private static async Task TestUnknownLengthStalePartialAsync()
        {
            Console.WriteLine("Test: unknown length restarts over a stale partial file");
            byte[] payload = MakePayload(700 * 1024);
            string expected = Hash(payload);
            string root = Path.Combine(Path.GetTempPath(), "alllinks-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            using (TestServer server = new TestServer(payload, false, 1, false))
            {
                AppSettings settings = NewSettings(root);
                try
                {
                    using (DownloadManager manager = new DownloadManager(settings))
                    {
                        DownloadTask task = manager.AddTask(server.BaseUrl + "/file.bin", "unknown.bin", "");
                        Directory.CreateDirectory(Path.GetDirectoryName(task.TempPath));
                        File.WriteAllBytes(task.TempPath, new byte[payload.Length + 256 * 1024]);

                        manager.Start(task);
                        bool finished = WaitFor(delegate
                        {
                            return task.State == TaskState.Completed || task.State == TaskState.Error;
                        }, 30000);

                        Assert(finished, "Unknown-length download timed out. State=" + task.State);
                        Assert(task.State == TaskState.Completed, "Unknown-length failed: " + task.LastError);
                        Assert(task.TotalBytes == payload.Length,
                            "Expected " + payload.Length + " bytes, got " + task.TotalBytes);
                        if (File.Exists(task.FinalPath))
                        {
                            Assert(new FileInfo(task.FinalPath).Length == payload.Length,
                                "Stale bytes were left in the final file");
                            Assert(Hash(File.ReadAllBytes(task.FinalPath)) == expected,
                                "Unknown-length bytes do not match");
                        }
                        else
                        {
                            Assert(false, "Unknown-length final file missing");
                        }
                    }
                }
                finally
                {
                    TryDelete(root);
                }
            }
            Console.WriteLine("  PASS");
            await Task.FromResult(0);
        }

        // --------------------------------------------------------------- support

        private static AppSettings NewSettings(string folder)
        {
            AppSettings settings = new AppSettings();
            settings.OutputFolder = folder;
            settings.ParallelFiles = 1;
            settings.SegmentsPerFile = 4;
            settings.Retries = 2;
            settings.AutoResume = false;
            settings.ExtraHeaders = "";
            return settings;
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        private static byte[] MakePayload(int size)
        {
            byte[] data = new byte[size];
            uint state = 0x12345678;
            for (int i = 0; i < size; i++)
            {
                state = state * 1664525u + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            return data;
        }

        private static string Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch
            {
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (condition) return;
            _failures++;
            Console.WriteLine("  FAIL: " + message);
        }

        // ------------------------------------------------------------ test server

        private sealed class TestServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private readonly byte[] _data;
            private readonly bool _ranges;
            private readonly int _delayMs;
            private readonly bool _sendLength;
            private volatile bool _stopped;

            public readonly string BaseUrl;

            public TestServer(byte[] data, bool ranges, int delayMs) : this(data, ranges, delayMs, true)
            {
            }

            public TestServer(byte[] data, bool ranges, int delayMs, bool sendLength)
            {
                _data = data;
                _ranges = ranges;
                _delayMs = delayMs;
                _sendLength = sendLength;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
                BaseUrl = "http://127.0.0.1:" + endpoint.Port;
                _thread = new Thread(AcceptLoop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            private void AcceptLoop()
            {
                while (!_stopped)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch
                    {
                        return;
                    }

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Handle(client);
                    });
                }
            }

            private void Handle(TcpClient client)
            {
                try
                {
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadHeaders(stream);
                        if (request.Length == 0) return;

                        string[] lines = request.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                        string[] parts = lines[0].Split(' ');
                        if (parts.Length < 2) return;

                        bool isHead = string.Equals(parts[0], "HEAD", StringComparison.OrdinalIgnoreCase);
                        long start = 0;
                        long end = _data.Length - 1;
                        bool requestedRange = false;

                        for (int i = 1; i < lines.Length; i++)
                        {
                            if (!lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                            string spec = lines[i].Substring(6).Trim();
                            if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) continue;

                            string[] pieces = spec.Substring(6).Split('-');
                            long parsedStart;
                            if (pieces.Length > 0 && long.TryParse(pieces[0], out parsedStart))
                            {
                                start = parsedStart;
                                requestedRange = true;
                                if (pieces.Length > 1 && pieces[1].Length > 0)
                                {
                                    long parsedEnd;
                                    if (long.TryParse(pieces[1], out parsedEnd)) end = parsedEnd;
                                }
                                if (end >= _data.Length) end = _data.Length - 1;
                            }
                        }

                        bool partial = _ranges && requestedRange;
                        if (!_ranges || !requestedRange)
                        {
                            start = 0;
                            end = _data.Length - 1;
                        }

                        if (start >= _data.Length)
                        {
                            string notSatisfiable = "HTTP/1.1 416 Requested Range Not Satisfiable\r\n" +
                                "Content-Range: bytes */" + _data.Length + "\r\n" +
                                "Content-Length: 0\r\nConnection: close\r\n\r\n";
                            byte[] errorBytes = Encoding.ASCII.GetBytes(notSatisfiable);
                            stream.Write(errorBytes, 0, errorBytes.Length);
                            return;
                        }

                        long length = end - start + 1;
                        StringBuilder headers = new StringBuilder();
                        headers.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                        headers.Append("Content-Type: application/octet-stream\r\n");
                        if (_sendLength)
                        {
                            headers.Append("Content-Length: ").Append(length).Append("\r\n");
                        }
                        if (_ranges)
                        {
                            headers.Append("Accept-Ranges: bytes\r\n");
                        }
                        if (partial)
                        {
                            headers.Append("Content-Range: bytes ").Append(start).Append('-')
                                .Append(end).Append('/').Append(_data.Length).Append("\r\n");
                        }
                        headers.Append("ETag: \"test-v1\"\r\n");
                        headers.Append("Connection: close\r\n\r\n");

                        byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        if (isHead) return;

                        // With no declared length the client cannot tell how much body to expect.
                        // The probe only inspects headers, so answer it with an empty body.
                        if (!_sendLength && requestedRange)
                        {
                            stream.Flush();
                            return;
                        }

                        const int chunk = 16 * 1024;
                        long position = start;
                        while (position <= end && !_stopped)
                        {
                            int count = (int)Math.Min((long)chunk, end - position + 1);
                            stream.Write(_data, (int)position, count);
                            position += count;
                            if (_delayMs > 0) Thread.Sleep(_delayMs);
                        }
                        stream.Flush();
                    }
                }
                catch
                {
                }
            }

            private static string ReadHeaders(NetworkStream stream)
            {
                StringBuilder sb = new StringBuilder();
                byte[] buffer = new byte[4096];
                while (sb.Length < 65536)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    if (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                }
                return sb.ToString();
            }

            public void Dispose()
            {
                _stopped = true;
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
