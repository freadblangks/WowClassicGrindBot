using Core;
using Core.Addon;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PathingAPI;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using BlazorTable;
using Core.Session;
using MatBlazor;
using SharedLib;
using Game;
using Core.Database;

namespace BlazorServer
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;

            var logfile = "out.log";
            var config = new LoggerConfiguration()
                //.Enrich.FromLogContext()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .WriteTo.LoggerSink()
                .WriteTo.File(logfile, rollingInterval: RollingInterval.Day)
                .WriteTo.Debug(outputTemplate: "[{Timestamp:HH:mm:ss:fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss:fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");

            Log.Logger = config.CreateLogger();
            Log.Logger.Debug("Startup()");

            while (WowProcess.Get() == null)
            {
                Log.Information("Unable to find the Wow process is it running ?");
                Thread.Sleep(1000);
            }
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var logger = new SerilogLoggerProvider(Log.Logger).CreateLogger(nameof(Program));
            services.AddSingleton(logger);

            var wowProcess = new WowProcess();
            var wowScreen = new WowScreen(logger, wowProcess);
            wowScreen.GetRectangle(out var rect);
            wowScreen.Dispose();

            var addonConfig = AddonConfig.Load();
            var addonConfigurator = new AddonConfigurator(logger, addonConfig);

            if (!addonConfig.IsDefault() && !addonConfigurator.Installed())
            {
                // At this point the webpage never loads so fallback to configuration page
                AddonConfig.Delete();
                DataFrameConfiguration.RemoveConfiguration();
            }

            if(DataFrameConfiguration.Exists() && 
                !DataFrameConfiguration.IsValid(rect, addonConfigurator.GetInstalledVersion()))
            {
                // At this point the webpage never loads so fallback to configuration page
                DataFrameConfiguration.RemoveConfiguration();
            }

            if (AddonConfig.Exists() && DataFrameConfiguration.Exists())
            {
                services.AddSingleton<IPPather>(x => GetPather(logger));
                services.AddSingleton<IBotController>(x => new BotController(logger, x.GetRequiredService<IPPather>(), DataConfig.Load(), Configuration));
                services.AddSingleton<IGrindSessionHandler>(x => x.GetRequiredService<IBotController>().GrindSessionHandler);
                services.AddSingleton<IGrindSession>(x => x.GetRequiredService<IBotController>().GrindSession);
                services.AddSingleton<IAddonReader>(x => x.GetRequiredService<IBotController>().AddonReader);
            }
            else
            {
                services.AddSingleton<IBotController>(x => new ConfigBotController());
                services.AddSingleton<IAddonReader>(x => new ConfigAddonReader());
            }

            services.AddMatBlazor();
            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddBlazorTable();
        }

        private IPPather GetPather(Microsoft.Extensions.Logging.ILogger logger)
        {
            var scp = new StartupConfigPathing();
            Configuration.GetSection(StartupConfigPathing.Position).Bind(scp);

            bool failed = false;
            var dataConfig = DataConfig.Load();

            if (scp.Type == StartupConfigPathing.Types.RemoteV3)
            {
                var api = new RemotePathingAPIV3(logger, scp.hostv3, scp.portv3, new WorldMapAreaDB(dataConfig));
                if (api.PingServer())
                {
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
                    Log.Debug($"Using {StartupConfigPathing.Types.RemoteV3}({api.GetType().Name}) {scp.hostv3}:{scp.portv3}");
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
                    return api;
                }
                api.Dispose();
                failed = true;
            }

            if (scp.Type == StartupConfigPathing.Types.RemoteV1 || failed)
            {
                var api = new RemotePathingAPI(logger, scp.hostv1, scp.portv1);
                var pingTask = Task.Run(api.PingServer);
                pingTask.Wait();
                if (pingTask.Result)
                {
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");

                    if (scp.Type == StartupConfigPathing.Types.RemoteV3)
                    {
                        Log.Debug($"Unavailable {StartupConfigPathing.Types.RemoteV3} {scp.hostv3}:{scp.portv3} - Fallback to {StartupConfigPathing.Types.RemoteV1}");
                    }

                    Log.Debug($"Using {StartupConfigPathing.Types.RemoteV1}({api.GetType().Name}) {scp.hostv1}:{scp.portv1}");
                    Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
                    return api;
                }

                failed = true;
            }

            Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
            if (scp.Type != StartupConfigPathing.Types.Local)
            {
                Log.Debug($"{scp.Type} not available!");
            }
            Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");

            var localApi = new LocalPathingApi(logger, new PPatherService(LogWrite, dataConfig));
            localApi.SelfTest();
            Log.Information($"Using {StartupConfigPathing.Types.Local}({localApi.GetType().Name}) pathing API.");
            Log.Information("++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++");
            return localApi;
        }

        public static void LogWrite(string message)
        {
            Log.Information(message);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public static void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStaticFiles();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}