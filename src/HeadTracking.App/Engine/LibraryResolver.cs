using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HeadTracking.App
{
    /// <summary>
    /// Finds the app's libraries in HeadTrackingApp\lib\, so HeadTracking.exe can sit alone in the
    /// SPT root with no .exe.config beside it (0.5.0; before, a config's probing path did this).
    /// Runs as the module initializer: before Main, before any library is needed. Whatever version
    /// is in lib\ is used, which also stands in for binding redirects.
    /// </summary>
    internal static class LibraryResolver
    {
        private static string _libraryDirectory;

        [ModuleInitializer]
        internal static void Install()
        {
            string exeDirectory = Path.GetDirectoryName(typeof(LibraryResolver).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            _libraryDirectory = Path.Combine(exeDirectory, AppPaths.DataFolderName, AppPaths.LibraryFolderName);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(_libraryDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>The C# compiler's marker for module initializers; .NET Framework does not ship it.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
