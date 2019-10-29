using System;
using System.IO;
using System.Reflection;
using System.Threading;

static class Program
{
    //[LoaderOptimization(LoaderOptimization.MultiDomainHost)]
    //[STAThread]
    static void Main()
    {
        // Get the startup path. Both assemblies (Loader and
        // MyApplication) reside in the same directory:
        var startupPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // cache path = directory where the assemblies get
        // (shadow) copied:
        var cachePath = Path.Combine(startupPath, "__cache");

        var assemblyPath = Path.Combine(startupPath, "MyApplication.exe");

        var setup = new AppDomainSetup
        {
            ApplicationName = "MyApplication",
            ShadowCopyFiles = "true", // note: it isn't a bool
            CachePath = cachePath
        };

        var domain = AppDomain.CreateDomain("MyApplication", AppDomain.CurrentDomain.Evidence, setup);

        var lockChecker = new Timer(_ => Console.WriteLine(IsFileLocked(assemblyPath)));

        lockChecker.Change(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1));

        domain.ExecuteAssembly(assemblyPath);

        Console.WriteLine();

        AppDomain.Unload(domain);

        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, true);
        }

        lockChecker.Dispose();
    }

    static bool IsFileLocked(string path)
    {
        FileStream stream = null;

        var file = new FileInfo(path);
        try
        {
            stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException)
        {
            //the file is unavailable because it is:
            //still being written to
            //or being processed by another thread
            //or does not exist (has already been processed)
            return true;
        }
        finally
        {
            if (stream != null)
                stream.Close();
        }

        //file is not locked
        return false;
    }
}
