using System;
using System.IO;

namespace FlaUiPoc
{
    /// <summary>
    /// Where things live. Nothing in this repo assumes a fixed install path:
    ///   PocDir  - this project's folder, found by walking up from the test assembly to FlaUiPoc.csproj
    ///             (override with QA_POC_DIR).
    ///   OutDir  - PocDir\TestResults: run artifacts, recordings, dashboard state.
    ///   ExePath - the Exatouch binary under test: EXATOUCH_EXE if set, else
    ///             %EXATOUCH_ROOT%\2.0.x\bin\Exatouch.exe, else C:\Dev\Exatouch\2.0.x\bin\Exatouch.exe.
    /// </summary>
    public static class Paths
    {
        public static readonly string PocDir = FindPocDir();
        public static readonly string OutDir = Path.Combine(PocDir, "TestResults");
        public static readonly string ExatouchRoot = Env("EXATOUCH_ROOT") ?? @"C:\Dev\Exatouch";
        public static readonly string ExePath = Env("EXATOUCH_EXE") ?? Path.Combine(ExatouchRoot, "2.0.x", "bin", "Exatouch.exe");

        private static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static string FindPocDir()
        {
            var env = Env("QA_POC_DIR");
            if (env != null) return env;
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FlaUiPoc.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
