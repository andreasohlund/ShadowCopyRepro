using System;
using System.IO;
using System.Reflection;

static class Program
{
    static void Main()
    {
        var basePath = @"C:\Users\andre\Documents\GitHub\repros\ShadowCopyRepro\MyApplication\bin\Debug";
        var assemblyFullPath = Path.Combine(basePath, "MyApplication.exe");

        var appDomainSetup = new AppDomainSetup
        {
            ApplicationName = "MyApplication",
            ShadowCopyFiles = bool.TrueString,
            ApplicationBase = basePath
        };

        var domain = AppDomain.CreateDomain(appDomainSetup.ApplicationName, AppDomain.CurrentDomain.Evidence, appDomainSetup);

        domain.ExecuteAssembly(assemblyFullPath);

        Console.WriteLine();

        AppDomain.Unload(domain);
    }
}
