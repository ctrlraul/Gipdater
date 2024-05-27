using System.IO.Compression;
using CommandLine;
using Newtonsoft.Json.Linq;

namespace AppUpdater;


// When working on this it's important to keep in mind it will copy itself over to TEMP directory when
// updating, so getting the current path, current file name and so on will not always return the same thing.


// ReSharper disable once ClassNeverInstantiated.Global
public class CommandLineOptions
{
    [Option('p', "game-path", Default = null, HelpText = "Path to game directory")]
    public string? GamePath { get; set; }

    public static CommandLineOptions From(string[] args)
    {
        ParserResult<CommandLineOptions> result = Parser.Default.ParseArguments<CommandLineOptions>(args);

        if (result.Tag == ParserResultType.Parsed)
            return ((Parsed<CommandLineOptions>)result).Value;
        
        IEnumerable<Error>? errors = ((NotParsed<CommandLineOptions>)result).Errors;
        IEnumerable<string> errorLines = errors.Select(error => "  " + error);
        throw new Exception($"Failed to parse options:\n{string.Join("\n", errorLines)}");
    }
}


// ReSharper disable once ClassNeverInstantiated.Global
internal class Program
{
    private const string RepoOwner = "Unreasonable-Games";
    private const string RepoName = "Ship-Game-Online-Client-Build";
    private const string RepoBranch = "main";
    // private const string GameExecutableName = "ShipGameOnline.exe";
    private const string GameExecutablePath = "ShipGameOnline.exe";

    private const string GitHubRepoZipUrl = $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/heads/{RepoBranch}.zip";
    private const string GitHubApiCommitsEndpoint = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits";
    
    private static readonly string TempDirPath = Path.GetTempPath();
    
    private static async Task Main(string[] args)
    {
        CommandLineOptions options = CommandLineOptions.From(args);

        if (IsRunningFromTemp())
        {
            if (options.GamePath is null)
                throw new Exception("Provide game-path argument when running from TEMP");
            
            await PullUpdate(options.GamePath);
            ExitAndLaunchGame();
        }
        
        if (!File.Exists(GameExecutablePath))
            ExitAndReRunFromTempToUpdate();
        
        DateTime currentUpdateDate = GetFileModifiedDate();
        DateTime latestUpdateDate = await GetLatestUpdateDate();
        
        if (latestUpdateDate > currentUpdateDate)
        {
            Console.WriteLine("Game is not up to date");
            ExitAndReRunFromTempToUpdate();
        }
        
        ExitAndLaunchGame();
    }

    private static async Task PullUpdate(string gameDirPath)
    {
        Console.WriteLine("Creating temporary update directory...");
        string tempUpdateDir = Path.Combine(TempDirPath, GenId());
        Directory.CreateDirectory(tempUpdateDir);
        
        Console.WriteLine("Downloading newest version...");
        string zipFilePath = Path.Combine(TempDirPath, GenId() + ".zip");
        await DownloadFile(GitHubRepoZipUrl, zipFilePath);
        
        Console.WriteLine("Extracting...");
        ExtractZip(zipFilePath, tempUpdateDir);
        
        Console.WriteLine("Deleting current version...");
        DeleteContentsInFolder(gameDirPath);
        
        Console.WriteLine("Installing new version...");
        const string intermediateDir = $"{RepoName}-{RepoBranch}";
        CopyDirectory(Path.Combine(tempUpdateDir, intermediateDir), gameDirPath);
        
        Console.WriteLine("Update complete!");
    }
    
    private static void DeleteContentsInFolder(string folderPath)
    {
        foreach (string file in Directory.GetFiles(folderPath))
        {
            File.Delete(file);
        }

        foreach (string subFolder in Directory.GetDirectories(folderPath))
        {
            DeleteContentsInFolder(subFolder);
            Directory.Delete(subFolder);
        }
    }

    private static void ExtractZip(string fromPath, string toPath)
    {
        ZipFile.ExtractToDirectory(fromPath, toPath, overwriteFiles: true);
    }
    
    private static bool IsRunningFromTemp()
    {
        string exeDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        string tempDirectory = TempDirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        return exeDirectory.StartsWith(tempDirectory);
    }
    
    private static void ExitAndLaunchGame()
    {
        if (File.Exists(GameExecutablePath))
            System.Diagnostics.Process.Start(GameExecutablePath);
        else
            Console.Error.WriteLine("Ended up without a game executable.");
        
        Environment.Exit(0);
    }
    
    private static void ExitAndReRunFromTempToUpdate()
    {
        Console.WriteLine("Copying updater to temporary directory to update from there...");

        if (Environment.ProcessPath is null)
            throw new Exception("Can't get own path");
        
        string currentExeName = Path.GetFileName(Environment.ProcessPath);
        string tempExePath = Path.Combine(TempDirPath, $"{GenId()}-{currentExeName}");
        
        File.Copy(Environment.ProcessPath, tempExePath);

        System.Diagnostics.Process.Start(tempExePath, "-p " + AppDomain.CurrentDomain.BaseDirectory);
        Environment.Exit(0);
    }
    
    private static string GenId()
    {
        return Guid.NewGuid().ToString();
    }
    
    private static DateTime GetFileModifiedDate()
    {
        FileInfo fileInfo = new FileInfo(Environment.ProcessPath);
        return fileInfo.LastWriteTime.ToUniversalTime();
    }
    
    private static async Task<DateTime> GetLatestUpdateDate()
    {
        using HttpClient client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "C# HttpClient");
        
        HttpResponseMessage response = await client.GetAsync(GitHubApiCommitsEndpoint);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        JArray commits = JArray.Parse(json);
        
        return DateTime.Parse(commits[0]["commit"]["committer"]["date"].ToString());
    }

    private static void CopyDirectory(string sourceDirPath, string destDirPath)
    {
        if (!Directory.Exists(destDirPath))
            Directory.CreateDirectory(destDirPath);

        foreach (string file in Directory.GetFiles(sourceDirPath))
        {
            string destFile = Path.Combine(destDirPath, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDirPath))
        {
            string destSubDir = Path.Combine(destDirPath, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private static async Task DownloadFile(string url, string downloadPath)
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloadedBytes = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        
        var buffer = new byte[4096];
        int bytesRead;
        
        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                double progress = (double)downloadedBytes / totalBytes * 100;
                Console.Write($"\rDownloaded {downloadedBytes}/{totalBytes} bytes ({progress:F2}%).");
            }
            else
            {
                Console.Write($"\rDownloaded {downloadedBytes} bytes.");
            }
        }

        Console.WriteLine("\nDownload completed!");
    }
}
