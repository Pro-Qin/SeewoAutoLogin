using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace SeewoAutoLogin.Services
{
    public static class AutoStartService
    {
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "SeewoAutoLogin";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                    if (key == null) return false;
                    var value = key.GetValue(AppName) as string;
                    return !string.IsNullOrEmpty(value);
                }
                catch { return false; }
            }
        }

        public static void Enable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                if (key == null) return;

                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                }
            }
            catch { }
        }

        public static void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                key?.DeleteValue(AppName, false);
            }
            catch { }
        }
    }
}
