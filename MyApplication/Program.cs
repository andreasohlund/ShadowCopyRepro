using NServiceBus;
using NServiceBus.Hosting.Helpers;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Initializing");

        var scanner = new AssemblyScanner();

        scanner.GetScannableAssemblies();

        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
}

namespace NServiceBus.Hosting.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Logging;

    /// <summary>
    /// Helpers for assembly scanning operations.
    /// </summary>
    public class AssemblyScanner
    {
        /// <summary>
        /// Creates a new scanner that will scan the base directory of the current <see cref="AppDomain" />.
        /// </summary>
        public AssemblyScanner()
            : this(AppDomain.CurrentDomain.BaseDirectory)
        {
        }

        /// <summary>
        /// Creates a scanner for the given directory.
        /// </summary>
        public AssemblyScanner(string baseDirectoryToScan)
        {
            this.baseDirectoryToScan = baseDirectoryToScan;
        }

        internal AssemblyScanner(Assembly assemblyToScan)
        {
            this.assemblyToScan = assemblyToScan;
        }

        /// <summary>
        /// Determines if the scanner should throw exceptions or not.
        /// </summary>
        public bool ThrowExceptions { get; set; } = true;

        /// <summary>
        /// Determines if the scanner should scan assemblies loaded in the <see cref="AppDomain.CurrentDomain" />.
        /// </summary>
        public bool ScanAppDomainAssemblies { get; set; } = true;

        internal string CoreAssemblyName { get; set; } = NServicebusCoreAssemblyName;

        /// <summary>
        /// Traverses the specified base directory including all sub-directories, generating a list of assemblies that should be
        /// scanned for handlers, a list of skipped files, and a list of errors that occurred while scanning.
        /// </summary>
        public AssemblyScannerResults GetScannableAssemblies()
        {
            var results = new AssemblyScannerResults();
            var processed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (assemblyToScan != null)
            {
                if (ScanAssembly(assemblyToScan, processed))
                {
                    AddTypesToResult(assemblyToScan, results);
                }

                return results;
            }

            if (ScanAppDomainAssemblies)
            {
                var appDomainAssemblies = AppDomain.CurrentDomain.GetAssemblies();

                foreach (var assembly in appDomainAssemblies)
                {
                    if (ScanAssembly(assembly, processed))
                    {
                        AddTypesToResult(assembly, results);
                    }
                }
            }

            var assemblies = new List<Assembly>();

            Console.WriteLine($"Scanning all asm in {baseDirectoryToScan} (Nested={ScanNestedDirectories})");

            foreach (var assemblyFile in ScanDirectoryForAssemblyFiles(baseDirectoryToScan, ScanNestedDirectories))
            {
                if (TryLoadScannableAssembly(assemblyFile.FullName, results, out var assembly))
                {
                    assemblies.Add(assembly);
                }
            }

            var platformAssembliesString = (string)AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

            if (platformAssembliesString != null)
            {
                var platformAssemblies = platformAssembliesString.Split(Path.PathSeparator);

                foreach (var platformAssembly in platformAssemblies)
                {
                    if (TryLoadScannableAssembly(platformAssembly, results, out var assembly))
                    {
                        assemblies.Add(assembly);
                    }
                }
            }

            foreach (var assembly in assemblies)
            {
                if (ScanAssembly(assembly, processed))
                {
                    AddTypesToResult(assembly, results);
                }
            }

            results.RemoveDuplicates();

            return results;
        }

        bool TryLoadScannableAssembly(string assemblyPath, AssemblyScannerResults results, out Assembly assembly)
        {
            assembly = null;

            if (IsExcluded(Path.GetFileNameWithoutExtension(assemblyPath)))
            {
                var skippedFile = new SkippedFile(assemblyPath, "File was explicitly excluded from scanning.");
                results.SkippedFiles.Add(skippedFile);

                return false;
            }

            assemblyValidator.ValidateAssemblyFile(assemblyPath, out var shouldLoad, out var reason);

            if (!shouldLoad)
            {
                var skippedFile = new SkippedFile(assemblyPath, reason);
                results.SkippedFiles.Add(skippedFile);

                return false;
            }

            try
            {
                Console.WriteLine($"Loading {assemblyPath}");
                assembly = Assembly.LoadFrom(assemblyPath);

                Console.WriteLine($"{assembly.FullName} loaded from location {assembly.Location}");

                return true;
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is FileLoadException)
            {
                results.ErrorsThrownDuringScanning = true;

                if (ThrowExceptions)
                {
                    var errorMessage = $"Could not load '{assemblyPath}'. Consider excluding that assembly from the scanning.";
                    throw new Exception(errorMessage, ex);
                }

                var skippedFile = new SkippedFile(assemblyPath, ex.Message);
                results.SkippedFiles.Add(skippedFile);

                return false;
            }
        }

        bool ScanAssembly(Assembly assembly, Dictionary<string, bool> processed)
        {
            if (assembly == null)
            {
                return false;
            }

            if (processed.TryGetValue(assembly.FullName, out var value))
            {
                return value;
            }

            processed[assembly.FullName] = false;

            if (assembly.GetName().Name == CoreAssemblyName)
            {
                processed[assembly.FullName] = true;
            }

            if (ShouldScanDependencies(assembly))
            {
                foreach (var referencedAssemblyName in assembly.GetReferencedAssemblies())
                {
                    var referencedAssembly = GetReferencedAssembly(referencedAssemblyName);
                    var referencesCore = ScanAssembly(referencedAssembly, processed);

                    if (referencesCore)
                    {
                        processed[assembly.FullName] = true;
                        break;
                    }
                }
            }

            return processed[assembly.FullName];
        }

        Assembly GetReferencedAssembly(AssemblyName assemblyName)
        {
            Assembly referencedAssembly = null;

            try
            {
                referencedAssembly = Assembly.Load(assemblyName);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is BadImageFormatException || ex is FileLoadException) { }

            if (referencedAssembly == null)
            {
                referencedAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            }

            return referencedAssembly;
        }

        internal static string FormatReflectionTypeLoadException(string fileName, ReflectionTypeLoadException e)
        {
            var sb = new StringBuilder($"Could not enumerate all types for '{fileName}'.");

            if (!e.LoaderExceptions.Any())
            {
                sb.NewLine($"Exception message: {e}");
                return sb.ToString();
            }

            var nsbAssemblyName = typeof(AssemblyScanner).Assembly.GetName();
            var nsbPublicKeyToken = BitConverter.ToString(nsbAssemblyName.GetPublicKeyToken()).Replace("-", string.Empty).ToLowerInvariant();
            var displayBindingRedirects = false;
            var files = new List<string>();
            var sbFileLoadException = new StringBuilder();
            var sbGenericException = new StringBuilder();

            foreach (var ex in e.LoaderExceptions)
            {
                var loadException = ex as FileLoadException;

                if (loadException?.FileName != null)
                {
                    var assemblyName = new AssemblyName(loadException.FileName);
                    var assemblyPublicKeyToken = BitConverter.ToString(assemblyName.GetPublicKeyToken()).Replace("-", string.Empty).ToLowerInvariant();
                    if (nsbAssemblyName.Name == assemblyName.Name &&
                        nsbAssemblyName.CultureInfo.ToString() == assemblyName.CultureInfo.ToString() &&
                        nsbPublicKeyToken == assemblyPublicKeyToken)
                    {
                        displayBindingRedirects = true;
                        continue;
                    }

                    if (!files.Contains(loadException.FileName))
                    {
                        files.Add(loadException.FileName);
                        sbFileLoadException.NewLine(loadException.FileName);
                    }
                    continue;
                }

                sbGenericException.NewLine(ex.ToString());
            }

            if (sbGenericException.Length > 0)
            {
                sb.NewLine("Exceptions:");
                sb.Append(sbGenericException);
            }

            if (sbFileLoadException.Length > 0)
            {
                sb.AppendLine();
                sb.NewLine("It looks like you may be missing binding redirects in the config file for the following assemblies:");
                sb.Append(sbFileLoadException);
                sb.NewLine("For more information see http://msdn.microsoft.com/en-us/library/7wd6ex19(v=vs.100).aspx");
            }

            if (displayBindingRedirects)
            {
                sb.AppendLine();
                sb.NewLine("Try to add the following binding redirects to the config file:");

                const string bindingRedirects = @"<runtime>
    <assemblyBinding xmlns=""urn:schemas-microsoft-com:asm.v1"">
        <dependentAssembly>
            <assemblyIdentity name=""NServiceBus.Core"" publicKeyToken=""9fc386479f8a226c"" culture=""neutral"" />
            <bindingRedirect oldVersion=""0.0.0.0-{0}"" newVersion=""{0}"" />
        </dependentAssembly>
    </assemblyBinding>
</runtime>";

                sb.NewLine(string.Format(bindingRedirects, nsbAssemblyName.Version.ToString(4)));
            }

            return sb.ToString();
        }

        static List<FileInfo> ScanDirectoryForAssemblyFiles(string directoryToScan, bool scanNestedDirectories)
        {
            var fileInfo = new List<FileInfo>();
            var baseDir = new DirectoryInfo(directoryToScan);
            var searchOption = scanNestedDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var searchPattern in FileSearchPatternsToUse)
            {
                foreach (var info in baseDir.GetFiles(searchPattern, searchOption))
                {
                    fileInfo.Add(info);
                }
            }
            return fileInfo;
        }

        bool IsExcluded(string assemblyNameOrFileName)
        {
            var isExplicitlyExcluded = AssembliesToSkip.Any(excluded => IsMatch(excluded, assemblyNameOrFileName));
            if (isExplicitlyExcluded)
            {
                return true;
            }

            var isExcludedByDefault = DefaultAssemblyExclusions.Any(exclusion => IsMatch(exclusion, assemblyNameOrFileName));
            if (isExcludedByDefault)
            {
                return true;
            }

            return false;
        }

        static bool IsMatch(string expression1, string expression2)
        {
            return DistillLowerAssemblyName(expression1) == DistillLowerAssemblyName(expression2);
        }

        bool IsAllowedType(Type type)
        {
            return type != null &&
                   !type.IsValueType &&
                   !IsCompilerGenerated(type) &&
                   !TypesToSkip.Contains(type);
        }

        static bool IsCompilerGenerated(Type type)
        {
            return type.GetCustomAttribute<CompilerGeneratedAttribute>(false) != null;
        }

        static string DistillLowerAssemblyName(string assemblyOrFileName)
        {
            var lowerAssemblyName = assemblyOrFileName.ToLowerInvariant();
            if (lowerAssemblyName.EndsWith(".dll") || lowerAssemblyName.EndsWith(".exe"))
            {
                lowerAssemblyName = lowerAssemblyName.Substring(0, lowerAssemblyName.Length - 4);
            }
            return lowerAssemblyName;
        }

        void AddTypesToResult(Assembly assembly, AssemblyScannerResults results)
        {
            try
            {
                //will throw if assembly cannot be loaded
                results.Types.AddRange(assembly.GetTypes().Where(IsAllowedType));
            }
            catch (ReflectionTypeLoadException e)
            {
                results.ErrorsThrownDuringScanning = true;

                var errorMessage = FormatReflectionTypeLoadException(assembly.FullName, e);
                if (ThrowExceptions)
                {
                    throw new Exception(errorMessage);
                }

                LogManager.GetLogger<AssemblyScanner>().Warn(errorMessage);
                results.Types.AddRange(e.Types.Where(IsAllowedType));
            }
            results.Assemblies.Add(assembly);
        }

        bool ShouldScanDependencies(Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return false;
            }

            var assemblyName = assembly.GetName();

            if (assemblyName.Name == CoreAssemblyName)
            {
                return false;
            }

            if (AssemblyValidator.IsRuntimeAssembly(assemblyName.GetPublicKeyToken()))
            {
                return false;
            }

            if (IsExcluded(assemblyName.Name))
            {
                return false;
            }

            return true;
        }

        AssemblyValidator assemblyValidator = new AssemblyValidator();
        internal List<string> AssembliesToSkip = new List<string>();
        internal bool ScanNestedDirectories;
        internal List<Type> TypesToSkip = new List<Type>();
        Assembly assemblyToScan;
        string baseDirectoryToScan;
        const string NServicebusCoreAssemblyName = "NServiceBus.Core";

        static string[] FileSearchPatternsToUse =
        {
            "*.dll",
            "*.exe"
        };

        //TODO: delete when we make message scanning lazy #1617
        static string[] DefaultAssemblyExclusions =
        {
            // NSB Build-Dependencies
            "nunit",
            "nunit.framework",
            "nunit.applicationdomain",

            // NSB OSS Dependencies
            "nlog",
            "newtonsoft.json",
            "common.logging",
            "nhibernate",

            // Raven
            "raven.client",
            "raven.abstractions",

            // Azure host process, which is typically referenced for ease of deployment but should not be scanned
            "NServiceBus.Hosting.Azure.HostProcess.exe",

            // And other windows azure stuff
            "Microsoft.WindowsAzure"
        };
    }
}

namespace NServiceBus.Hosting.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    /// <summary>
    /// Holds <see cref="AssemblyScanner.GetScannableAssemblies" /> results.
    /// Contains list of errors and list of scannable assemblies.
    /// </summary>
    public class AssemblyScannerResults
    {
        /// <summary>
        /// Constructor to initialize AssemblyScannerResults.
        /// </summary>
        public AssemblyScannerResults()
        {
            Assemblies = new List<Assembly>();
            Types = new List<Type>();
            SkippedFiles = new List<SkippedFile>();
        }

        /// <summary>
        /// List of successfully found and loaded assemblies.
        /// </summary>
        public List<Assembly> Assemblies { get; private set; }

        /// <summary>
        /// List of files that were skipped while scanning because they were a) explicitly excluded
        /// by the user, b) not a .NET DLL, or c) not referencing NSB and thus not capable of implementing
        /// <see cref="IHandleMessages{T}" />.
        /// </summary>
        public List<SkippedFile> SkippedFiles { get; }

        /// <summary>
        /// True if errors where encountered during assembly scanning.
        /// </summary>
        public bool ErrorsThrownDuringScanning { get; internal set; }

        /// <summary>
        /// List of types.
        /// </summary>
        public List<Type> Types { get; private set; }

        internal void RemoveDuplicates()
        {
            Assemblies = Assemblies.Distinct().ToList();
            Types = Types.Distinct().ToList();
        }
    }
}

namespace NServiceBus
{
    using System;
    using System.Reflection;

    class AssemblyValidator
    {
        public void ValidateAssemblyFile(string assemblyPath, out bool shouldLoad, out string reason)
        {
            try
            {
                var token = AssemblyName.GetAssemblyName(assemblyPath).GetPublicKeyToken();

                if (IsRuntimeAssembly(token))
                {
                    shouldLoad = false;
                    reason = "File is a .NET runtime assembly.";
                    return;
                }
            }
            catch (BadImageFormatException)
            {
                shouldLoad = false;
                reason = "File is not a .NET assembly.";
                return;
            }

            shouldLoad = true;
            reason = "File is a .NET assembly.";
        }


        public static bool IsRuntimeAssembly(byte[] publicKeyToken)
        {
            var tokenString = BitConverter.ToString(publicKeyToken).Replace("-", string.Empty).ToLowerInvariant();

            //Compare token to known Microsoft tokens

            if (tokenString == "b77a5c561934e089")
            {
                return true;
            }

            if (tokenString == "7cec85d7bea7798e")
            {
                return true;
            }

            if (tokenString == "b03f5f7f11d50a3a")
            {
                return true;
            }

            if (tokenString == "31bf3856ad364e35")
            {
                return true;
            }

            if (tokenString == "cc7b13ffcd2ddd51")
            {
                return true;
            }

            if (tokenString == "adb9793829ddae60")
            {
                return true;
            }

            return false;
        }
    }
}

namespace NServiceBus
{
    using System;
    using System.Text;

    static class StringBuilderExtensions
    {
        /// <summary>
        /// Appends a new line marker to the StringBuilder followed by the text in the <paramref name="newLine"/>.
        /// </summary>
        public static StringBuilder NewLine(this StringBuilder stringBuilder, string newLine)
        {
            return stringBuilder.Append(Environment.NewLine).Append(newLine);
        }
    }
}

namespace NServiceBus.Hosting.Helpers
{
    /// <summary>
    /// Contains information about a file that was skipped during scanning along with a text describing
    /// the reason why the file was skipped.
    /// </summary>
    public class SkippedFile
    {
        internal SkippedFile(string filePath, string message)
        {
            FilePath = filePath;
            SkipReason = message;
        }

        /// <summary>
        /// The full path to the file that was skipped.
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Description of the reason why this file was skipped.
        /// </summary>
        public string SkipReason { get; }
    }
}