using System;
using System.IO;
using System.Reflection;
using Enva.Libs;

namespace Enva.Commands;

public class InitCommand
{
    private const string EmbeddedResourceName = "enva.yaml.example";

    /// <summary>
    /// Writes the embedded example enva.yaml to the target path.
    /// </summary>
    /// <param name="outputPath">Path for the new config file (default: enva.yaml in current directory).</param>
    /// <param name="force">If true, overwrite existing file.</param>
    /// <returns>0 on success, 1 on error.</returns>
    public static int Run(string outputPath, bool force)
    {
        var logger = Logger.GetLogger("init");

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Directory.GetCurrentDirectory(), "enva.yaml");
        }
        else
        {
            outputPath = Path.GetFullPath(outputPath);
        }

        if (File.Exists(outputPath) && !force)
        {
            logger.Printf("File already exists: {0}", outputPath);
            logger.Printf("Use --force to overwrite.");
            return 1;
        }

        var assembly = Assembly.GetExecutingAssembly();
        Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null)
        {
            logger.Printf("Example config resource not found.");
            return 1;
        }

        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (stream)
            using (Stream file = File.Create(outputPath))
            {
                stream.CopyTo(file);
            }

            logger.Printf("Created config: {0}", outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Printf("Failed to write config: {0}", ex.Message);
            return 1;
        }
    }
}
