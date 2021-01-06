//-----------------------------------------------------------------------------
// Filename: Startup.cs
//
// Description: Startup and configuration for SIP/Web server application. 
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 29 Dec 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using demo.DataAccess;

namespace demo
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging();

            // DB Context factory is used by the SIP servers.
            services.AddDbContextFactory<SIPAssetsDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("SIPAssets")));

            // DB Context is used directly by web API controllers.
            services.AddDbContext<SIPAssetsDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("SIPAssets")));

            services.AddSingleton(typeof(SIPHostedService));
            services.AddHostedService<SIPHostedService>();
            services.AddControllers()
                .AddNewtonsoftJson(options =>
                options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore
            );
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "demo", Version = "v1" });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            //if (env.IsDevelopment())
            //{
                app.UseDeveloperExceptionPage();
           // }

            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "demo v1"));
            // Using the welcome page breaks the API routes.
            //app.UseWelcomePage();

            //app.UseHttpsRedirection();

            app.UseRouting();

            //app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });

            SIPSorcery.LogFactory.Set(app.ApplicationServices.GetService<ILoggerFactory>());
        }
    }
}
