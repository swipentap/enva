using System;

namespace Enva.CLI;

public static class Files
{
    public static FileOps NewFileOps()
    {
        return FileOps.NewFileOps();
    }
}

public class FileOps
{
    private string? path;
    private string? content;
    private string? mode;
    private bool parents;
    private bool append;
    private string operation = "";
    private bool recursive;
    private bool force = true;
    private bool allowGlob;

    public static FileOps NewFileOps()
    {
        return new FileOps();
    }

    public FileOps Recursive()
    {
        recursive = true;
        return this;
    }

    public FileOps Force(bool value)
    {
        force = value;
        return this;
    }

    public FileOps AllowGlob()
    {
        allowGlob = true;
        return this;
    }

    public FileOps SuppressErrors()
    {
        return this;
    }

    public FileOps Append()
    {
        append = true;
        return this;
    }

    public FileOps Mkdir(string dirPath, bool createParents = false)
    {
        path = dirPath;
        parents = createParents;
        operation = "mkdir";
        return this;
    }

    public FileOps Write(string filePath, string fileContent)
    {
        path = filePath;
        content = fileContent;
        append = false;
        operation = "write";
        return this;
    }

    public string Chmod(string filePath, string permissions)
    {
        return $"chmod {permissions} {Quote(filePath)}";
    }

    public string Remove(string filePath)
    {
        string flags = "";
        if (recursive)
        {
            flags += "r";
        }
        if (force)
        {
            flags += "f";
        }
        string flagPart = flags != "" ? $"-{flags} " : "";
        string quotedPath = allowGlob ? filePath : Quote(filePath);
        return $"rm {flagPart}{quotedPath}";
    }

    public string Truncate(string filePath)
    {
        return $"truncate -s 0 {Quote(filePath)}";
    }

    public string Symlink(string target, string linkPath)
    {
        return $"ln -s {Quote(target)} {Quote(linkPath)}";
    }

    public string FindDelete(string directory, string pattern, string fileType)
    {
        string patternEscaped = EscapeSingleQuotes(pattern);
        return $"find {Quote(directory)} -type {fileType} -name '{patternEscaped}' -delete";
    }

    public string ToCommand()
    {
        if (operation == "mkdir")
        {
            string flag = parents ? "-p " : "";
            return $"mkdir {flag}{Quote(path!)}";
        }
        if (operation == "write")
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(content))
            {
                throw new InvalidOperationException("Path and content must be set for write operation");
            }
            string sanitized = content.Replace("\\", "\\\\");
            sanitized = EscapeSingleQuotes(sanitized);
            string redir = append ? ">>" : ">";
            return $"printf '{sanitized}' {redir} {Quote(path)}";
        }
        throw new InvalidOperationException("Invalid FileOps operation");
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$") || s.Contains("'"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }

    private string EscapeSingleQuotes(string value)
    {
        return value.Replace("'", "'\"'\"'");
    }
}
