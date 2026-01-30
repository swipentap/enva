using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public static class Users
{
    public static User NewUser()
    {
        return User.NewUser();
    }
}

public class User
{
    private string? username;
    private string shell = "/bin/bash";
    private List<string> groups = new List<string>();
    private bool createHome = true;

    public static User NewUser()
    {
        return new User();
    }

    public User Username(string name)
    {
        username = name;
        return this;
    }

    public User Shell(string path)
    {
        shell = path;
        return this;
    }

    public User Groups(IEnumerable<string> groupList)
    {
        groups = groupList.ToList();
        return this;
    }

    public User CreateHome(bool value)
    {
        createHome = value;
        return this;
    }

    public string CheckExists()
    {
        if (string.IsNullOrEmpty(username))
        {
            throw new InvalidOperationException("Username must be set");
        }
        return $"id -u {Quote(username)}";
    }

    public string Add()
    {
        if (string.IsNullOrEmpty(username))
        {
            throw new InvalidOperationException("Username must be set");
        }
        List<string> parts = new List<string> { "useradd" };
        if (createHome)
        {
            parts.Add("-m");
        }
        parts.Add("-s");
        parts.Add(Quote(shell));
        if (groups.Any())
        {
            string groupSpec = string.Join(",", groups);
            parts.Add("-G");
            parts.Add(Quote(groupSpec));
        }
        parts.Add(Quote(username));
        return string.Join(" ", parts);
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$") || s.Contains("'"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }
}
