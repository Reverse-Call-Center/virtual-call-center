using Microsoft.Extensions.Options;
using System.Text.Json;
using virtual_call_center.Models;

namespace virtual_call_center.Services;

public class ConfigurationManager
{
    private readonly ILogger<ConfigurationManager> _logger;
    private readonly string _configDirectory;
    private SIPConfig _sipConfig = null!;
    private IvrConfig _ivrConfig = null!;
    private QueueConfig _queueConfig = null!;
    private FileSystemWatcher _watcher = null!;

    public ConfigurationManager(ILogger<ConfigurationManager> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configDirectory = configuration.GetValue<string>("ConfigDirectory") ?? "config";
        
        if (!Directory.Exists(_configDirectory))
            Directory.CreateDirectory(_configDirectory);

        LoadConfigurations();
        SetupFileWatcher();
    }

    public SIPConfig SipConfig => _sipConfig;
    public IvrConfig IvrConfig => _ivrConfig;
    public QueueConfig QueueConfig => _queueConfig;

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    private void LoadConfigurations()
    {
        _sipConfig = LoadConfig<SIPConfig>("sip.json", new SIPConfig());
        _ivrConfig = LoadConfig<IvrConfig>("ivr.json", new IvrConfig());
        _queueConfig = LoadConfig<QueueConfig>("queue.json", new QueueConfig());
        
        _logger.LogInformation("Loaded configurations from {Directory}", _configDirectory);
    }

    /// <summary>
    /// Loads configuration from the specified file or creates default if not found
    /// </summary>
    private T LoadConfig<T>(string filename, T defaultConfig) where T : class
    {
        var filePath = Path.Combine(_configDirectory, filename);
        
        if (!File.Exists(filePath))
        {
            SaveConfig(filename, defaultConfig);
            _logger.LogWarning("Created default configuration file: {FilePath}", filePath);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            });
            return config ?? defaultConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration from {FilePath}", filePath);
            return defaultConfig;
        }
    }

    private void SaveConfig<T>(string filename, T config) where T : class
    {
        var filePath = Path.Combine(_configDirectory, filename);
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration to {FilePath}", filePath);
        }
    }

    private void SetupFileWatcher()
    {
        _watcher = new FileSystemWatcher(_configDirectory, "*.json");
        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        Task.Delay(100).ContinueWith(_ =>
        {
            _logger.LogInformation("Configuration file changed: {FileName}", e.Name ?? "Unknown");
            LoadConfigurations();
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(e.Name ?? "Unknown"));
        });
    }

    public IvrConfig.IvrNode? GetIvrNode(int action)
    {
        return _ivrConfig.Nodes.FirstOrDefault(n => n.Action == action);
    }

    public QueueConfig.Queue? GetQueue(int action)
    {
        return _queueConfig.Queues.FirstOrDefault(q => q.Action == action);
    }
}

public class ConfigurationChangedEventArgs : EventArgs
{
    public string FileName { get; }
    
    public ConfigurationChangedEventArgs(string fileName)
    {
        FileName = fileName;
    }
}
