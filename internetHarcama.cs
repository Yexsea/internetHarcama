using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

class Program
{
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
    }

    static ConcurrentDictionary<int, NetData> networkUsage = new();
    static ConcurrentDictionary<int, string> processNames = new();

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
            catch (Exception)
            {
                Console.WriteLine("Bu programın çalışması için yönetici yetkisine ihtiyacı var");
                Console.WriteLine("Çıkmak için bir tuşa basın...");
                Console.ReadKey();
            }
            return; 
        }

        
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

                Console.WriteLine($"Ağ Kullanımı - {DateTime.Now:HH:mm:ss}".PadRight(80));
                Console.WriteLine("".PadRight(80));
                Console.WriteLine($"{"PID",-8}{"Süreç Adı",-20}{"İndirme",-15}{"Yükleme",-15}{"Toplam",-15}");
                Console.WriteLine(new string('-', 80));

                displayList.Clear();

                foreach (var kvp in networkUsage)
                {
                    int pid = kvp.Key;
                    long currentDown = kvp.Value.TotalDown;
                    long currentUp = kvp.Value.TotalUp;
                    long totalUsed = currentDown + currentUp;

                    if (!lastTotals.TryGetValue(pid, out var last))
                    {
                        last = (0, 0);
                    }

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
                            pName = $"Bilinmiyor ({pid})";
                            processNames[pid] = pName;
                        }
                    }

                    displayList.Add(new DisplayItem
                    {
                        Pid = pid,
                        Name = pName,
                        SpeedDown = speedDown,
                        SpeedUp = speedUp,
                        TotalUsed = totalUsed
                    });
                }

                var topProcesses = displayList.OrderByDescending(x => x.TotalUsed).Take(20).ToList();

                foreach (var p in topProcesses)
                {
                    string line = $"{p.Pid,-8}{TrancateString(p.Name, 18),-20}{FormatSpeed(p.SpeedDown),-15}{FormatSpeed(p.SpeedUp),-15}{FormatMB(p.TotalUsed),-15}";
                    Console.WriteLine(line.PadRight(80));
                }

                
                for (int i = topProcesses.Count; i < 20; i++)
                {
                    Console.WriteLine("".PadRight(80));
                }
            }
            catch (Exception)
            {
                
            }

            Thread.Sleep(1000);
        }
    }

    static bool IsRunAsAdmin()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void StartEtwSession()
    {
        try
        {
            
            string sessionName = "NetworkMonitorSession_" + Guid.NewGuid().ToString();
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
            Console.Clear();
            Console.WriteLine("ETW Oturumu başlatılamadı");
            Console.WriteLine(ex.Message);
        }
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