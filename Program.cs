using System.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using virtual_call_center.Ivr;
using virtual_call_center.Services;
using ConfigManager = virtual_call_center.Services.ConfigurationManager;

namespace virtual_call_center;

public class Program
{
    private static ILogger<Program> _logger = null!;
    
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();
        builder.Services.AddLogging();
        
        builder.Services.AddSingleton<ConfigManager>();
        builder.Services.AddSingleton<AudioService>();
        builder.Services.AddSingleton<AgentManager>();
        builder.Services.AddSingleton<CallRouter>();
        builder.Services.AddSingleton<CallSessionManager>();
        builder.Services.AddSingleton<DTMFDetector>();
        builder.Services.AddSingleton<DynamicAgentManager>();
        builder.Services.AddSingleton<SIPActions>();

        var app = builder.Build();
        
        _logger = app.Services.GetRequiredService<ILogger<Program>>();

        app.MapGrpcService<AgentService>();
        
        app.MapGet("/",
            () =>
                "Virtual Call Center - Communication with gRPC endpoints must be made through a gRPC client.");

        Task.Run(() => InitializeSIP(app.Services));
        
        _logger.LogInformation("Virtual Call Center starting up at {Timestamp}", DateTime.UtcNow);
        
        app.Run();
    }

    /// <summary>
    /// Initializes the SIP server and sets up incoming call handling
    /// </summary>
    private static void InitializeSIP(IServiceProvider services)
    {
        var configManager = services.GetRequiredService<ConfigManager>();
        var sipActions = services.GetRequiredService<SIPActions>();
        
        var sipConfig = configManager.SipConfig;
        
        _logger.LogInformation("Starting SIP server on {Address}:{Port}", sipConfig.BindAddress, sipConfig.Port);
        
        var sipTransport = new SIPTransport();
        sipTransport.AddSIPChannel(new SIPUDPChannel(IPAddress.Parse(sipConfig.BindAddress), sipConfig.Port));
        
        var userAgent = new SIPUserAgent(sipTransport, null);

        userAgent.OnIncomingCall += async (agent, sipRequest) =>
        {
            try
            {
                await sipActions.OnIncomingCall(agent, sipRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling incoming call");
            }
        };
        
        _logger.LogInformation("SIP server initialized and listening for calls");
    }
}