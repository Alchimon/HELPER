using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic;

static class Gemini {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Data = Path.Combine(Root, "GeminiProfile");
    static object Import() {
        string url = Interaction.InputBox("Paste your HTTPS subscription URL. It is not saved.", "Gemini Portable", "");
        if (url.Length == 0) return null;
        try {
            Uri address = new Uri(url.Trim());
            if (address.Scheme != "https") throw new FormatException();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.AllowAutoRedirect = false;
            request.Timeout = 15000;
            string body;
            using (var response = (HttpWebResponse)request.GetResponse()) {
                if (response.StatusCode != HttpStatusCode.OK) throw new FormatException();
                using (var reader = new StreamReader(response.GetResponseStream())) {
                    var buffer = new char[1048577];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (count == buffer.Length) throw new FormatException();
                    body = new string(buffer, 0, count);
                }
            }
            if (!body.Contains("vless://")) {
                body = body.Trim().Replace('-', '+').Replace('_', '/');
                body = body.PadRight((body.Length + 3) / 4 * 4, '=');
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
            }
            foreach (string line in body.Split('\n')) {
                Uri u; Guid id;
                if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out u) || u.Scheme != "vless" || u.Port < 1 || !Guid.TryParse(u.UserInfo, out id)) continue;
                var q = System.Web.HttpUtility.ParseQueryString(u.Query);
                string sec = q["security"], type = q["type"], header = q["headerType"];
                if (sec != "reality" && sec != "tls") continue;
                if (!String.IsNullOrEmpty(type) && type != "tcp" && type != "raw") continue;
                if (!String.IsNullOrEmpty(header) && header != "none") continue;
                if (!String.IsNullOrEmpty(q["encryption"]) && q["encryption"] != "none") continue;
                var stream = new Dictionary<string, object> { {"network", "raw"}, {"security", sec} };
                if (sec == "reality") {
                    if (String.IsNullOrEmpty(q["pbk"]) || String.IsNullOrEmpty(q["sni"])) continue;
                    stream["realitySettings"] = new { password = q["pbk"], serverName = q["sni"], fingerprint = q["fp"] ?? "chrome", shortId = q["sid"] ?? "", spiderX = q["spx"] ?? "" };
                } else {
                    var tls = new Dictionary<string, object> { {"serverName", q["sni"] ?? u.DnsSafeHost} };
                    if (!String.IsNullOrEmpty(q["alpn"])) tls["alpn"] = q["alpn"].Split(',');
                    if (!String.IsNullOrEmpty(q["fp"])) tls["fingerprint"] = q["fp"];
                    stream["tlsSettings"] = tls;
                }
                return new { protocol = "vless", streamSettings = stream, settings = new { vnext = new[] { new { address = u.DnsSafeHost, port = u.Port, users = new[] { new { id = id.ToString(), encryption = "none", flow = q["flow"] ?? "" } } } } } };
            }
        } catch (Exception) { }
        throw new InvalidOperationException("Could not import subscription. Use a standard HTTPS subscription with VLESS TCP Reality/TLS, no redirect.");
    }
    [STAThread]
    static void Main(string[] args) {
        Process proxy = null;
        string temp = null;
        try {
            Directory.CreateDirectory(Data);
            using (var locked = new FileStream(Path.Combine(Data, "running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                string chrome = Path.Combine(Root, "Browser", "chrome.exe"), xray = Path.Combine(Root, "Core", "xray.exe");
                if (!File.Exists(chrome) || !File.Exists(xray)) throw new InvalidOperationException("Extract the entire portable archive before starting Gemini.");
                string saved = Path.Combine(Data, "profile.json");
                object outbound;
                if (!File.Exists(saved) || Array.IndexOf(args, "--reset") >= 0 || (Control.ModifierKeys & Keys.Shift) != 0) {
                    outbound = Import();
                    if (outbound == null) return;
                    File.WriteAllText(saved, Json.Serialize(outbound));
                } else outbound = Json.DeserializeObject(File.ReadAllText(saved));
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                temp = Path.Combine(Data, Guid.NewGuid() + ".json");
                File.WriteAllText(temp, Json.Serialize(new { log = new { loglevel = "none" }, inbounds = new[] { new { listen = "127.0.0.1", port = port, protocol = "http" } }, outbounds = new[] { outbound } }));
                proxy = Process.Start(new ProcessStartInfo(xray, "run -config \"" + temp + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(xray) });
                bool ready = false;
                for (int i = 0; i < 50 && !proxy.HasExited; i++) {
                    try { using (var client = new TcpClient()) client.Connect(IPAddress.Loopback, port); ready = true; break; }
                    catch (SocketException) { Thread.Sleep(100); }
                }
                if (!ready || proxy.HasExited) throw new InvalidOperationException("Xray did not start. Check whether the subscription profile is supported.");
                string flags = "--user-data-dir=\"" + Path.Combine(Data, "Chromium") + "\" --proxy-server=http://127.0.0.1:" + port + " --proxy-bypass-list=\"<-loopback>\" --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE 127.0.0.1\" --app=https://gemini.google.com/";
                using (var browser = Process.Start(new ProcessStartInfo(chrome, flags) { UseShellExecute = false })) browser.WaitForExit();
            }
        } catch (IOException) { MessageBox.Show("Close any running Gemini window and check that this folder is writable.", "Gemini Portable"); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Gemini Portable"); }
        finally {
            if (proxy != null) { try { if (!proxy.HasExited) { proxy.Kill(); proxy.WaitForExit(); } } catch (InvalidOperationException) {} finally { proxy.Dispose(); } }
            if (temp != null) try { File.Delete(temp); } catch (IOException) {} catch (UnauthorizedAccessException) {}
        }
    }
}
