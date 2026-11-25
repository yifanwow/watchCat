// 程序备注：右键程序 → "写备注说明"。存在 notes.json（只在本机，不上传），会替换"说明"列里的默认说明
using System;
using System.Collections.Generic;
using System.IO;

namespace WatchDog
{
    partial class Monitor
    {
        Dictionary<string, string> notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string NotesPath { get { return Path.Combine(RulesDir, "notes.json"); } }

        void LoadNotes()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(NotesPath))
                {
                    Dictionary<string, object> d = Json.Parse(Json.ReadFile(NotesPath)) as Dictionary<string, object>;
                    if (d != null) foreach (KeyValuePair<string, object> kv in d) if (kv.Value is string && ((string)kv.Value).Length > 0) m[kv.Key] = (string)kv.Value;
                }
            }
            catch { }
            notes = m;
        }
        void SaveNotes()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> kv in notes) d[kv.Key] = kv.Value;
            try { Json.WriteFile(NotesPath, d); } catch (Exception ex) { Say("备注没能保存：" + ex.Message); }
        }

        // 说明：自己写的备注优先，其次是内置的中文说明
        string DescOf(string name)
        {
            string s;
            if (name == null) return null;
            if (!demoMode && notes.TryGetValue(name, out s)) return s;   // 演示截图时不显示自己写的备注
            if (Desc.TryGetValue(name, out s)) return s;
            return AutoDesc(name);
        }
        // 内置说明里没有的程序：用 exe 自带的"文件说明"（比如 Photoshop → Adobe Photoshop 2025），服务用它的显示名。每个名字只查一次
        Dictionary<string, string> autoDesc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string AutoDesc(string name)
        {
            string s;
            if (autoDesc.TryGetValue(name, out s)) return s;
            try
            {
                if (name.StartsWith("svc:"))
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name.Substring(4)))
                        s = k == null ? null : k.GetValue("DisplayName") as string;
                    if (s != null && s.StartsWith("@")) s = null;   // "@%SystemRoot%\...dll,-123" 这种要从资源里取，不管它
                }
                else
                {
                    string path = PathOf(name);
                    if (path == null) return null;   // 进程刚退出 / 还没拿到：不记下来，下次再试
                    if (System.IO.File.Exists(path))
                    {
                        System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        s = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim() : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
                    }
                }
                if (s != null && (s.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase))) s = null;   // 说明就是程序名本身：没意义
                if (s != null && s.Length > 40) s = s.Substring(0, 40) + "…";
            }
            catch { s = null; }
            autoDesc[name] = s;
            return s;
        }
        bool HasNote(string name) { return name != null && notes.ContainsKey(name); }

        void EditNote(string name)
        {
            if (name == null) return;
            string cur; notes.TryGetValue(name, out cur);
            string def; Desc.TryGetValue(name, out def);
            string ans = Prompt("备注",
                "给「" + name + "」写一句备注（会显示在\"说明\"列里）。\n\n" + (def != null ? "默认说明：" + def + "\n" : "") + "留空并确定 = 删除备注，恢复默认说明。",
                cur ?? def ?? "");
            if (ans == null) return;   // 点了取消
            ans = ans.Trim();
            if (ans.Length == 0 && cur == null) return;
            if (ans.Length == 0 || ans == def) notes.Remove(name); else notes[name] = ans.Length > 60 ? ans.Substring(0, 60) : ans;
            SaveNotes();
            RefreshAll();
        }
    }
}
