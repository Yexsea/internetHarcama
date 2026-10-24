using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

class Program
{
    // --- KONSOL DONMASINI ENGELLEYEN API'LER ---
    const uint ENABLE_QUICK_EDIT = 0x0040;
    const int STD_INPUT_HANDLE = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    // --- UYARI PENCERESİ İÇİN API ---
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    class NetData
    {
        public long TotalDown;
        public long TotalUp;
        public void AddDown(int bytes) => Interlocked.Add(ref TotalDown, bytes);
        public void AddUp(int bytes) => Interlocked.Add(ref TotalUp, bytes);
    }

    struct DisplayItem
    {
        public int Pid;
        public string Name;
        public long SpeedDown;
        public long SpeedUp;
        public long TotalUsed;
        public string Status;
    }

    static ConcurrentDictionary<int, NetData> networkUsage = new ConcurrentDictionary<int, NetData>();
    static ConcurrentDictionary<int, string> processNames = new ConcurrentDictionary<int, string>();

    // --- LİSTELER ---
    static HashSet<int> killedProcesses = new HashSet<int>();
    static HashSet<int> pendingPrompts = new HashSet<int>();
    static HashSet<int> ignoredProcesses = new HashSet<int>(); // YENİ: Hayır denilenleri hatırlamak için
    static readonly object actionLock = new object();

    // Sınır: 25 MB (25 * 1024 * 1024)
    const long LIMIT_BYTES = 25 * 1024 * 1024;
    static string etwErrorMessage = "";

    // Kapatılmaması gereken kritik işlemler
    static readonly HashSet<string> CriticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "smss", "csrss", "wininit", "services", "lsass",
        "winlogon", "system", "idle", "explorer", "dwm", "taskmgr", "Bilinmiyor"
    };

    static void Main()
    {
        if (!IsRunAsAdmin())
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(startInfo);
            }
            catch
            {
                Console.WriteLine("Bu programın çalışması için yönetici yetkisine ihtiyacı var.");
                Console.ReadKey();
            }
            return;
        }

        DisableConsoleQuickEdit();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.CursorVisible = false;

        Task.Run(() => StartEtwSession());

        var lastTotals = new Dictionary<int, (long down, long up)>();
        var displayList = new List<DisplayItem>(100);

        while (true)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
                Console.WriteLine($"Ağ Kullanımı & Görev Yöneticisi - {DateTime.Now:HH:mm:ss}".PadRight(80));

                if (!string.IsNullOrEmpty(etwErrorMessage))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[HATA]: {etwErrorMessage}".PadRight(80));
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("".PadRight(80));
                }

                Console.WriteLine($"{"PID",-8}{"Süreç Adı",-20}{"İndirme",-12}{"Yükleme",-12}{"Toplam",-12}{"Durum",-15}");
                Console.WriteLine(new string('-', 80));

                displayList.Clear();

                foreach (var kvp in networkUsage)
                {
                    int pid = kvp.Key;
                    long currentDown = kvp.Value.TotalDown;
                    long currentUp = kvp.Value.TotalUp;
                    long totalUsed = currentDown + currentUp;

                    if (!lastTotals.TryGetValue(pid, out var last)) last = (0, 0);

                    long speedDown = currentDown - last.down;
                    long speedUp = currentUp - last.up;
                    lastTotals[pid] = (currentDown, currentUp);

                    if (!processNames.TryGetValue(pid, out string pName))
                    {
                        try
                        {
                            pName = Process.GetProcessById(pid).ProcessName;
                            processNames[pid] = pName;
                        }
                        catch
                        {
                            pName = "Bilinmiyor";
                            processNames[pid] = pName;
                        }
                    }

                    // 25 MB SINIRI AŞILDIYSA MÜDAHALE ET
                    if (totalUsed > LIMIT_BYTES)
                    {
                        CheckAndKillProcess(pid, pName);
                    }

                    bool isKilled = false;
                    bool isIgnored = false;
                    lock (actionLock)
                    {
                        isKilled = killedProcesses.Contains(pid);
                        isIgnored = ignoredProcesses.Contains(pid);
                    }

                    string statusMsg = "Aktif";
                    if (isKilled) statusMsg = "[KAPATILDI]";
                    else if (isIgnored) statusMsg = "Aktif (İzinli)";

                    displayList.Add(new DisplayItem
                    {
                        Pid = pid,
                        Name = pName,
                        SpeedDown = speedDown,
                        SpeedUp = speedUp,
                        TotalUsed = totalUsed,
                        Status = statusMsg
                    });
                }

                var topProcesses = displayList.OrderByDescending(x => x.TotalUsed).Take(20).ToList();

                foreach (var p in topProcesses)
                {
                    string statusColor = (p.Status == "Aktif" || p.Status == "Aktif (İzinli)") ? "" : " (X)";
                    string line = $"{p.Pid,-8}{TrancateString(p.Name, 18),-20}{FormatSpeed(p.SpeedDown),-12}{FormatSpeed(p.SpeedUp),-12}{FormatMB(p.TotalUsed),-12}{p.Status + statusColor,-15}";
                    Console.WriteLine(line.PadRight(80));
                }

                for (int i = topProcesses.Count; i < 20; i++)
                {
                    Console.WriteLine("".PadRight(80));
                }
            }
            catch { }

            Thread.Sleep(1000);
        }
    }

    static void CheckAndKillProcess(int pid, string processName)
    {
        // Kritik işlemleri veya kendi programımızı kapatma
        if (CriticalProcesses.Contains(processName) || pid == Process.GetCurrentProcess().Id) return;

        lock (actionLock)
        {
            // Eğer kapatıldıysa, halihazırda soruluyorsa veya kullanıcı "Hayır" deyip yoksaydıysa tekrar sorma
            if (killedProcesses.Contains(pid) || pendingPrompts.Contains(pid) || ignoredProcesses.Contains(pid)) return;
            pendingPrompts.Add(pid);
        }

        Task.Run(() =>
        {
            string message = $"{processName} (PID: {pid}) 25 MB internet kullanım sınırını aştı!\n\nBu işlemi sonlandırmak ister misiniz?";

            int result = MessageBox(IntPtr.Zero, message, "25 MB Sınırı Aşıldı!", 0x00040000 | 0x00000030 | 0x00000004);

            if (result == 6) // Kullanıcı "Evet" seçerse
            {
                ExecuteTaskKill(pid, processName);
                lock (actionLock) { killedProcesses.Add(pid); }
            }
            else // Kullanıcı "Hayır" seçerse veya pencereyi kapatırsa
            {
                // Yoksayılanlar listesine ekle ki bir daha sormasın
                lock (actionLock) { ignoredProcesses.Add(pid); }
            }

            // Soru sorma işlemi bittiği için bekleyenler listesinden çıkar
            lock (actionLock) { pendingPrompts.Remove(pid); }
        });
    }

    static void ExecuteTaskKill(int pid, string processName)
    {
        try
        {
            ProcessStartInfo killInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c echo {processName} (PID: {pid}) kapatiliyor... & taskkill -f -pid {pid} & timeout /t 3",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(killInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Görev sonlandırılamadı: {ex.Message}");
        }
    }

    static void DisableConsoleQuickEdit()
    {
        IntPtr consoleHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (GetConsoleMode(consoleHandle, out uint consoleMode))
        {
            consoleMode &= ~ENABLE_QUICK_EDIT;
            SetConsoleMode(consoleHandle, consoleMode);
        }
    }

    static void StartEtwSession()
    {
        try
        {
            string sessionName = "NetBlockerEtwSession25MB";

            if (TraceEventSession.GetActiveSessionNames().Contains(sessionName))
            {
                using (var oldSession = new TraceEventSession(sessionName))
                {
                    oldSession.Dispose();
                }
            }

            using (var session = new TraceEventSession(sessionName))
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

                session.Source.Kernel.TcpIpRecv += (data) =>
                {
                    if (data.ProcessID <= 0) return;
                    networkUsage.GetOrAdd(data.ProcessID, _ => new NetData()).AddDown(data.size);
                };
                session.Source.Kernel.UdpIpRecv += (data) =>
                {
                    if (data.ProcessID <= 0) return;
                    networkUsage.GetOrAdd(data.ProcessID, _ => new NetData()).AddDown(data.size);
                };
                session.Source.Kernel.TcpIpSend += (data) =>
                {
                    if (data.ProcessID <= 0) return;
                    networkUsage.GetOrAdd(data.ProcessID, _ => new NetData()).AddUp(data.size);
                };
                session.Source.Kernel.UdpIpSend += (data) =>
                {
                    if (data.ProcessID <= 0) return;
                    networkUsage.GetOrAdd(data.ProcessID, _ => new NetData()).AddUp(data.size);
                };

                session.Source.Process();
            }
        }
        catch (Exception ex)
        {
            etwErrorMessage = ex.Message;
        }
    }

    static bool IsRunAsAdmin()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    static string TrancateString(string value, int maxChars) => value.Length <= maxChars ? value : value.Substring(0, maxChars) + "..";

    static string FormatSpeed(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B/s";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB/s";
        return $"{bytes / (1024.0 * 1024.0):F2} MB/s";
    }

    static string FormatMB(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return $"{mb:F2} MB";
    }
}